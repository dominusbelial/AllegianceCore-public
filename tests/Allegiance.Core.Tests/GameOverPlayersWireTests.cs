using System.Buffers.Binary;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using PlayerScore = global::Allegiance.Server.PlayerScore;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P194 — <c>GAME_OVER_PLAYERS</c>(63), the per-player endgame broadcast that
/// fires alongside <c>GAME_OVER</c>(62) when a mission ends (P178/P191's shared
/// <c>Server.DeclareGameOver</c> hook). Wire codec + the three required live
/// proofs: (a) a game ending broadcasts the full per-player list
/// whole-mission alongside GAME_OVER; (b) a player on the <b>losing</b> side is
/// included with their own correct <c>sideId</c> (not just the winner's
/// roster); (c) a game ending in mission A never broadcasts it to mission B's
/// peers (reuses the established multi-mission proof pattern).
/// [Collection("LiveNetwork")] keeps this suite sequential against the other
/// live suites.
/// </summary>
[Collection("LiveNetwork")]
public sealed class GameOverPlayersWireTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private static readonly Vector3 Far = new(0f, 680f, 740f);          // outside DockRadius
    private static readonly Vector3 AtTarget = new(0f, 0f, -1000f);     // dist 0 < DockRadius → capture

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "GameOver Players Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static Cfg ConfigTwoGames() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Game A", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
            new Gm { Name = "Game B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Gamma", "Delta"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 944u, DwCookie = 0x944u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9770000000000000000ul, DrmHash = "goplayers",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static PlayerScore PlayerScoreOf(Srv server, FltBot bot) =>
        server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).PlayerScore;

    // ── codec ──────────────────────────────────────────────────────────────

    /// <summary>⭐ The codec round-trips its whole payload: a per-player list
    /// of <c>{characterName, sideId, rank, score}</c> — P193's PlayerScore — is
    /// unchanged through <see cref="GameOverPlayers.Encode"/>/<see cref="GameOverPlayers.Decode"/>.</summary>
    [Fact]
    public void GameOverPlayers_round_trips_payload()
    {
        var entries = new[]
        {
            new GameOverPlayerEntry("AlphaAce", 0, 3, 812.5f, 9, 4, 1, 2, 1, 0, 3, 1),
            new GameOverPlayerEntry("BravoBomber", 1, 7, 991.25f, 2, 5, 0, 1, 0, 2, 0, 0),
            new GameOverPlayerEntry("long-name-0123456789abcdef", 1, 0, 0f, 0, 0, 0, 0, 0, 0, 0, 0),  // 26 chars > 24 → truncated to 24 + NUL
        };
        var d = GameOverPlayers.Decode(new GameOverPlayers { Entries = entries }.Encode());

        Assert.Equal(GameOverPlayers.Fmid, d.GetFmid());
        Assert.Equal(3, d.Entries.Length);
        Assert.Equal("AlphaAce", d.Entries[0].CharacterName);
        Assert.Equal((short)0, d.Entries[0].SideId);
        Assert.Equal((short)3, d.Entries[0].Rank);
        Assert.Equal(812.5f, d.Entries[0].Score);
        Assert.Equal((short)9, d.Entries[0].Kills);
        Assert.Equal((short)4, d.Entries[0].Deaths);
        Assert.Equal((short)1, d.Entries[0].Ejections);
        Assert.Equal((short)2, d.Entries[0].Rescues);
        Assert.Equal((short)1, d.Entries[0].BaseKills);
        Assert.Equal((short)0, d.Entries[0].BaseCaptures);
        Assert.Equal((short)3, d.Entries[0].Artifacts);
        Assert.Equal((short)1, d.Entries[0].Flags);
        Assert.Equal("BravoBomber", d.Entries[1].CharacterName);
        Assert.Equal((short)1, d.Entries[1].SideId);
        Assert.Equal((short)7, d.Entries[1].Rank);
        Assert.Equal(991.25f, d.Entries[1].Score);
        Assert.Equal((short)2, d.Entries[1].Kills);
        Assert.Equal((short)2, d.Entries[1].BaseCaptures);
        Assert.Equal("long-name-0123456789abcd", d.Entries[2].CharacterName);  // 24 chars, NUL-terminated
        Assert.Equal(0f, d.Entries[2].Score);
    }

    /// <summary>Pins the resolved record stride (52 bytes) and the fixed
    /// message region (8 bytes) with discrete offsets, so a layout regression
    /// names the exact drifted field. Record layout: char[25] @0, pad @25,
    /// short sideId @26, short rank @28, pad @30, float score @32, short kills
    /// @36, short deaths @38, short ejections @40, short rescues @42, short
    /// baseKills @44, short baseCaptures @46, short artifacts @48, short flags
    /// @50 → 52.</summary>
    [Fact]
    public void GameOverPlayers_layout_pins_stride_and_offsets()
    {
        Assert.Equal(52, GameOverPlayerEntry.Size);
        Assert.Equal(8, GameOverPlayers.FixedSize);

        var msg = new GameOverPlayers
        {
            Entries = [new GameOverPlayerEntry("A", 905, 811, 777.25f, 1, 2, 3, 4, 5, 6, 7, 8)],
        };
        byte[] b = msg.Encode();
        Assert.Equal(8 + 52, b.Length);

        Assert.Equal(b.Length, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(0, 2)));  // cbmsg
        Assert.Equal(63, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2, 2)));        // fmid
        Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(4, 2)));         // ib (blob at 8)
        Assert.Equal(52, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6, 2)));        // cb = 1 × 52

        Assert.Equal((byte)'A', b[8]);        // characterName[0]
        Assert.Equal(0, b[9]);                // name NUL
        Assert.Equal(0, b[33]);               // pad @25 (record-relative) aligning sideId short to even
        Assert.Equal(905, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(8 + 26, 2)));   // sideId @26
        Assert.Equal(811, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(8 + 28, 2)));   // rank @28
        Assert.Equal(0, b[38]); Assert.Equal(0, b[39]);   // pad @30 aligning score float to 4
        Assert.Equal(BitConverter.SingleToUInt32Bits(777.25f), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8 + 32, 4))); // score @32
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(8 + 36, 2)));      // kills @36
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(8 + 38, 2)));      // deaths @38
        Assert.Equal(3, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(8 + 40, 2)));      // ejections @40
        Assert.Equal(4, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(8 + 42, 2)));      // rescues @42
        Assert.Equal(5, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(8 + 44, 2)));      // baseKills @44
        Assert.Equal(6, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(8 + 46, 2)));      // baseCaptures @46
        Assert.Equal(7, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(8 + 48, 2)));      // artifacts @48
        Assert.Equal(8, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(8 + 50, 2)));      // flags @50
    }

    // ── live: win-by-elimination (P178) fires the broadcast alongside GAME_OVER ──

    /// <summary>⭐ The P178 elimination path shares the <c>DeclareGameOver</c>
    /// hook, so a win-by-elimination (a side emptying) broadcasts
    /// GAME_OVER_PLAYERS to the surviving mission peer <b>alongside</b>
    /// GAME_OVER. The departed player is NOT in the list (this port has no
    /// m_oldPlayers recently-disconnected tracking — the deliberate
    /// simplification), so the surviving peer's single message carries exactly
    /// its own entry.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Win_by_elimination_broadcasts_game_over_players_to_surviving_peer()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass ready-up

        using var a = new FltBot();
        using var b = new FltBot();
        a.Connect(server.BoundPort, "Ace");
        a.SendLogonRequest(Request("Ace"));
        b.Connect(server.BoundPort, "Bee");
        b.SendLogonRequest(Request("Bee"));
        int got = PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "both bots must seat");

        // Seed a distinct non-zero score/rank so the wire provably carries them.
        // P583: Score is now COMPUTED at game over (FinalizeMissionScores), so
        // we seed real counters (a rescue + a kill) instead of the old raw-score
        // write, and assert the wire carries the computed PlayerScore.Score.
        PlayerScoreOf(server, a).Rank = 4;
        PlayerScoreOf(server, a).AddRescue();
        PlayerScoreOf(server, a).AddKill();

        // Advance the sim ~3 s so PlayerScore.TimePlayed > 0 — CalculateScore
        // (faithful to common.cpp:2669) early-returns when TimePlayed==0 and
        // leaves Score at 0; a non-zero time lets the P583 compute actually run.
        for (int i = 0; i < 60; i++) server.TickSim();

        a.ResetGameOvers();
        a.ResetGameOverPlayerBroadcasts();

        // P439 — the shared game-over path rotates _missions[0]; the Over-stage
        // assertions target the ORIGINAL mission object.
        var mission0 = server.Missions[0];

        // Rotation replaces the survivor's ship assignment asynchronously; retain the old match identity.
        short aceSide = server.Ships.First(s => s.Ship.Id == a.ShipAssignment!.Ship.ShipId).Ship.SideId!.Value;
        b.Dispose();                                    // empties Bee's side → Ace's side wins
        int seen = PumpUntil(server, () => mission0.Stage == MissionStage.Over
                                           && a.GameOvers.Count > 0 && a.GameOverPlayerBroadcasts.Count > 0, 900);
        Assert.True(seen < 900, "elimination win must broadcast GAME_OVER_PLAYERS");

        Assert.Equal(MissionStage.Over, mission0.Stage);
        Assert.True(a.GameOvers.Count > 0, "GAME_OVER must accompany GAME_OVER_PLAYERS");

        var gop = Assert.Single(a.GameOverPlayerBroadcasts);
        var entry = Assert.Single(gop.Entries);         // only the still-connected peer
        Assert.Equal("Ace", entry.CharacterName);
        Assert.Equal(aceSide, entry.SideId);
        Assert.Equal((short)4, entry.Rank);
        Assert.True(entry.Score > 0, "P583 game-over roster must carry a computed non-zero score");
        Assert.Equal((short)1, entry.Rescues);
        Assert.Equal((short)1, entry.Kills);

        Console.WriteLine($"P194-ELIM-LIVE: elimination win -> survivor {entry.CharacterName} (side {entry.SideId}) " +
                          $"saw GAME_OVER + GAME_OVER_PLAYERS [1 entry], rank {entry.Rank}, score {entry.Score}");
    }

    // ── live: conquest (P191) lists EVERY connected player, losing side included ──

    /// <summary>⭐ Requirement (a)+(b) together: a conquest win on a mission
    /// where BOTH players remain connected broadcasts GAME_OVER_PLAYERS
    /// whole-mission alongside GAME_OVER, listing EVERY connected player —
    /// including the player on the LOSING side, with their own correct
    /// <c>sideId</c> (not just the winner's roster). Uses the P191 conquest
    /// path because win-by-elimination always empties the losing side; here the
    /// loser is still connected when the game ends.</summary>
    [Trait("Requires", "Artwork")]
    [Trait("Lane", "LiveTiming")]   // P647 quarantine: flaky live-wire game-over roster even in isolation
    [SkippableFact]
    public void Conquest_win_lists_every_connected_player_including_losing_side()
    {
        Require.Artwork();
        using var seat = Seat(new Spec(true, Far), new Spec(false, Far));   // slot0 side A (capturer), slot1 side B
        var capturer = seat.Ships[0];
        var mission = seat.Server.Missions[0];

        // Distinct non-zero rank per player so the wire provably carries each.
        // P583: Score is COMPUTED at game over — seed real counters instead so
        // each player's computed score/kills differ and are carried distinctly.
        PlayerScoreOf(seat.Server, seat.Bots[0]).Rank = 5;
        PlayerScoreOf(seat.Server, seat.Bots[0]).AddKill();
        PlayerScoreOf(seat.Server, seat.Bots[0]).AddRescue();
        PlayerScoreOf(seat.Server, seat.Bots[1]).Rank = 902;
        PlayerScoreOf(seat.Server, seat.Bots[1]).AddRescue();

        foreach (var b in seat.Bots) { b.ResetGameOvers(); b.ResetGameOverPlayerBroadcasts(); }

        // Fly the boardable side-A capturer into the side-B flag's dock radius
        // → capture → recompute → conquest win (side A).
        // P450 (shipIGC.cpp:849-851): capture needs the base's shields down.
        DropShield(mission.Sectors[0].StationRuntimes.First(st => st.SideId == seat.SideB));
        capturer.Position = AtTarget;
        int seen = PumpUntil(seat.Server,
            () => mission.Stage == MissionStage.Over
                  && seat.Bots.All(b => b.GameOvers.Count > 0 && b.GameOverPlayerBroadcasts.Count > 0), 9000);
        Assert.True(seen < 9000, "conquest win must broadcast GAME_OVER_PLAYERS to every peer");
        Assert.Equal(MissionStage.Over, mission.Stage);

        // Every peer saw GAME_OVER alongside it (shared hook).
        Assert.True(seat.Bots.All(b => b.GameOvers.Count > 0), "GAME_OVER must accompany GAME_OVER_PLAYERS");

        foreach (var bot in seat.Bots)
        {
            var gop = Assert.Single(bot.GameOverPlayerBroadcasts);   // one whole-mission broadcast each
            Assert.Equal(2, gop.Entries.Length);                     // BOTH connected players listed

            var ace = gop.Entries.Single(e => e.CharacterName == "p194_0");   // winner, side A
            Assert.Equal(seat.SideA, ace.SideId);
            Assert.Equal((short)5, ace.Rank);
            Assert.True(ace.Score > 0, "P583 winner must carry a computed non-zero score");
            Assert.Equal((short)1, ace.Kills);
            Assert.Equal((short)1, ace.Rescues);

            var bee = gop.Entries.Single(e => e.CharacterName == "p194_1");   // loser, side B — INCLUDED
            Assert.Equal(seat.SideB, bee.SideId);                             // its OWN side, not the winner's
            Assert.Equal((short)902, bee.Rank);
            Assert.True(bee.Score > 0, "P583 loser must carry a computed non-zero score");
            Assert.Equal((short)1, bee.Rescues);
        }

        Console.WriteLine($"P194-CONQ-LIVE: conquest win -> every peer ({seat.Bots.Length}) got GAME_OVER_PLAYERS " +
                          $"with both players [A:{seat.SideA}, B:{seat.SideB}] — losing side included");
    }

    // ── live: multi-mission scoping ─────────────────────────────────────────

    /// <summary>⭐ Requirement (c): a conquest win in mission A broadcasts
    /// GAME_OVER_PLAYERS only to mission A's peers — a peer seated on mission B
    /// hears nothing and mission B stays Started (reuses the P178/P191
    /// multi-mission proof pattern).</summary>
    [Trait("Requires", "Artwork")]
    [Trait("Lane", "LiveTiming")]   // P647 quarantine: flaky live-wire game-over scope even in isolation
    [SkippableFact]
    public void Conquest_win_in_one_mission_does_not_leak_game_over_players_to_another()
    {
        Require.Artwork();
        using var server = new Srv();
        var core = Core();
        bool placedA = false;
        int seq = 0;
        server.ShipSpawner = (_, m, shipId) =>
        {
            var mission = seq == 1 ? server.Missions[1] : server.Missions[0];
            var sector = mission.Sectors[0];
            if (!placedA && ReferenceEquals(mission, server.Missions[0]))
            {
                short a0 = mission.Sides[0].Id, a1 = mission.Sides[1].Id;
                mission.ConquestEnabled = true;            // P191: conquest only on mission A
                mission.PlaceStation(FlagStation(core, sector.Id, a0, 8021, new Vector3(0, 0, -3000)), sector);
                mission.PlaceStation(FlagStation(core, sector.Id, a0, 8071, new Vector3(0, 0, -7000)), sector);
                mission.PlaceStation(FlagStation(core, sector.Id, a1, 8999, new Vector3(0, 0, -1000)), sector);
                placedA = true;
            }
            short sideIdx = seq == 1 ? (short)0 : (short)(seq == 0 ? 0 : 1);   // A0→A/s0, B0→B/s0, A1→A/s1
            var hull = seq == 0
                ? core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmBoard))  // the capturer (mission A, side0)
                : core.HullTypes.Values.OrderBy(h => h.HitPoints).First();
            var ship = new Ship(shipId, hull, new Vector3(0f, 920f, 960f), Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides[sideIdx].Id;
            mission.PlaceShip(ship, sector);
            seq++;
            return (ship, sector);
        };
        server.Boot(ConfigTwoGames());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        var port = server.BoundPort;

        using var a0 = new FltBot(); using var b0 = new FltBot(); using var a1 = new FltBot();
        var bots = new[] { a0, b0, a1 };
        try
        {
            string[] names = { "mA0", "mB0", "mA1" };
            // The spawner assigns mission/side by spawn slot. Finish each join
            // before opening the next so receive-thread scheduling cannot swap
            // the named mission-A and mission-B peers.
            for (int i = 0; i < 3; i++)
            {
                bots[i].Connect(port, names[i]);
                bots[i].SendLogonRequest(Request(names[i]));
                Assert.True(PumpUntil(server, () => bots[i].ShipAssignment is not null, 4000) < 4000,
                    $"peer {names[i]} must seat in its intended spawn slot");
            }
            int got = PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(got < 4000, "no assignments");
            // Spawn sequence routes peers to missions; socket drain order may differ.
            var peersA = bots.Where(b => ReferenceEquals(server.Ships.Single(s =>
                s.Ship.Id == b.ShipAssignment!.Ship.ShipId).Ship.Mission, server.Missions[0])).ToArray();
            Assert.Equal(2, peersA.Length);
            var peerB = Assert.Single(bots.Except(peersA));
            foreach (var s in server.Ships) s.Ship.Velocity = Vector3.Zero;

            var missionA = server.Missions[0];
            var capturer = server.Ships.First(s => ReferenceEquals(s.Ship.Mission, missionA)
                                                   && s.Ship.HullType.HasCapability(DataHullTypeIGC.HabmBoard)).Ship;
            var target = missionA.Sectors[0].StationRuntimes.First(st => st.SideId != capturer.SideId);

            foreach (var b in bots) { b.ResetGameOvers(); b.ResetGameOverPlayerBroadcasts(); }
            // P450 (shipIGC.cpp:849-851): capture needs the base's shields down.
            DropShield(target);
            capturer.Position = new Vector3(0, 0, -1000);   // at the side-1 flag target in mission A

            // Wait for both result kinds at every actually assigned mission-A peer.
            int seen = PumpUntil(server, () => missionA.Stage == MissionStage.Over
                && peersA.All(b => b.GameOvers.Count > 0 && b.GameOverPlayerBroadcasts.Count > 0), 9000);
            Assert.True(seen < 9000, "mission A did not end by conquest");
            Assert.Equal(MissionStage.Over, missionA.Stage);
            Assert.Equal(MissionStage.Started, server.Missions[1].Stage);

            // Mission A peers got the per-player broadcast...
            Assert.True(peersA.All(b => b.GameOverPlayerBroadcasts.Count > 0),
                "mission A peers must receive GAME_OVER_PLAYERS");

            // ...mission B's peer heard NOTHING (whole-mission scoped per mission).
            Assert.Empty(peerB.GameOverPlayerBroadcasts);

            Console.WriteLine($"P194-MULTI-LIVE: mission A conquest win -> A peers saw " +
                              $"{peersA[0].GameOverPlayerBroadcasts.Count}/{peersA[1].GameOverPlayerBroadcasts.Count} GAME_OVER_PLAYERS; " +
                              $"mission B peer saw {peerB.GameOverPlayerBroadcasts.Count}, stage {server.Missions[1].Stage}");
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }

    // ── harness ─────────────────────────────────────────────────────────────

    private sealed record Spec(bool Boardable, Vector3 Pos);

    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship[] Ships = null!;
        public FltBot[] Bots = null!;
        public short SideA, SideB;
        public bool StationsPlaced;

        public void Dispose()
        {
            foreach (var b in Bots) b?.Dispose();
            Server?.Dispose();
        }
    }

    private static Station FlagStation(StaticCore core, short sectorId, short side, int id, Vector3 pos)
    {
        var baseType = core.StationTypes.Values.First();
        var type = baseType with
        {
            ModelName = "P699_SYNTHETIC_CONQUEST", // synthetic 40-unit base, no artwork bay

            // P550 — counts for victory + landable/capturable (native dock gate
            // shipIGC.cpp:725-727 needs a land bit for any capture/dock).
            SabmCapabilities = DataStationTypeIGC.SabmFlag | DataStationTypeIGC.SabmLand | DataStationTypeIGC.SabmCapLand,
            MaxArmorHitPoints = 9999f, MaxShieldHitPoints = 100f,
            ShieldRegeneration = 0f,   // P450: capture needs shields < c_fcidDownedShield; no regen keeps a knocked base capturable
            Radius = 40,
        };
        var data = new DataStationIGC(
            Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: sectorId, SideId: side, StationId: (short)id,
            StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: $"FLAG_{id}");
        return new Station(data, type);
    }

    /// <summary>P450 (native shipIGC.cpp:849-851): a hostile board hull only
    /// captures when the base's shields are below c_fcidDownedShield (0.1). The
    /// synthetic flag bases are given a real shield, so the test drops it through
    /// the real damage path before the capturer arrives — mirroring
    /// StationCaptureLiveTests' knock-out.</summary>
    private static void DropShield(Station st) =>
        st.ReceiveDamage(0, 100f, 0.0, Vector3.Zero, Vector3.Zero, launcher: null);

    /// <summary>A booted single-mission conquest harness that plants the flag
    /// stations once (2 side-A + 1 side-B in sector 0, joining the real map's
    /// side0/side1 flags → side A 60%, side B 40%) and seats the given specs
    /// round-robin by side (slot i → side i % 2). Slot 0 is the boardable side-A
    /// capturer; the winning condition is captured via <c>AtTarget</c>. Mirrors
    /// ConquestTerritoryLiveTests.Seat.</summary>
    private static Seated Seat(params Spec[] specs)
    {
        var seat = new Seated();
        var core = Core();
        seat.Server = new Srv();
        seat.Ships = new Ship[specs.Length];

        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            if (!seat.StationsPlaced)
            {
                seat.SideA = mission.Sides[0].Id;
                seat.SideB = mission.Sides[1].Id;
                mission.ConquestEnabled = true;         // P191: opt the conquest win condition in
                mission.PlaceStation(FlagStation(core, sector.Id, seat.SideA, 8211, new Vector3(0, 0, -3000)), sector);
                mission.PlaceStation(FlagStation(core, sector.Id, seat.SideA, 8299, new Vector3(0, 0, -7600)), sector);
                mission.PlaceStation(FlagStation(core, sector.Id, seat.SideB, 8333, new Vector3(0, 0, -1000)), sector);
                seat.StationsPlaced = true;
            }
            var spec = specs[placed];
            DataHullTypeIGC hull = spec.Boardable
                ? core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmBoard))
                : core.HullTypes.Values.OrderBy(h => h.HitPoints).First();
            var ship = new Ship(id, hull, spec.Pos, Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B
            mission.PlaceShip(ship, sector);
            seat.Ships[placed] = ship;
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        foreach (var __m in seat.Server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass ready-up
        var port = seat.Server.BoundPort;

        int n = specs.Length;
        seat.Bots = new FltBot[n];
        try
        {
            for (int i = 0; i < n; i++)
            {
                seat.Bots[i] = new FltBot();
                seat.Bots[i].Connect(port, $"p194_{i}");
                seat.Bots[i].SendLogonRequest(Request($"p194_{i}"));
                // Preserve the documented bot[i] == spawn-slot[i] contract.
                Assert.True(PumpUntil(seat.Server, () => seat.Bots[i].ShipAssignment is not null, 4000) < 4000,
                    $"peer p194_{i} must seat in spawn slot {i}");
                Assert.Equal(seat.Ships[i].Id, seat.Bots[i].ShipAssignment!.Ship.ShipId);
            }
            PumpUntil(seat.Server, () => seat.Bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(seat.Bots.All(b => b.ShipAssignment is not null), "all bots must be assigned a ship");
            Assert.All(seat.Ships, s => Assert.NotNull(s));
            foreach (var s in seat.Ships) s.Velocity = Vector3.Zero;
            Assert.True(seat.SideA != seat.SideB, "harness needs two distinct sides");
            return seat;
        }
        catch
        {
            foreach (var b in seat.Bots) b?.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }
}
