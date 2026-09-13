using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P191 — live loopback proof of the full conquest/territory <b>wire chain</b>:
/// a hostile boardable ship captures an enemy <c>Flag</c>-capable station
/// (reusing P126's capture path), the mission's recompute
/// (<see cref="Mission.RecomputeConquestAndTerritory"/>) re-runs, and the server
/// (a) broadcasts a real <c>FM_S_GAME_STATE(155)</c> to the WHOLE mission when a
/// side's conquest/territory changed, and (b) declares the win through P178's
/// shared <see cref="Wire.GameOver"/>(62) path when a side crosses the threshold
/// — the mission flips <see cref="MissionStage.Over"/>. Companion pure-domain
/// proofs live in <see cref="ConquestTerritoryTests"/>.
/// </summary>
/// <remarks>
/// The real map contributes two <c>Flag</c>-capable stations (#1051 side0 in
/// Ceto, #1052 side1 in Quaoar); the harness additionally plants three synthetic
/// <c>Flag</c>-capable stations in sector 0 (2 side A + 1 side B = the capture
/// target), so the mission holds 5 such stations: side A owns 3 (60%), side B 2
/// (40%). Capturing the side B target hands it to side A → 4/5 = 80% ≥ the 65%
/// threshold → side A wins. Construction mirrors
/// <see cref="StationCaptureLiveTests"/>' 3-bot / 2-side harness.
/// [Collection("LiveNetwork")] keeps this suite sequential against the other
/// live suites.
/// </remarks>
[Collection("LiveNetwork")]
public sealed class ConquestTerritoryLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static readonly Vector3 StationSpot = new(0f, 0f, -1000f);  // the side-B capture target sits here
    private static readonly Vector3 Far = new(0f, 680f, 740f);          // well outside DockRadius
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
            new Gm { Name = "Conquest Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
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

    /// <summary>⭐ Multi-mission scoping: the whole-mission GAME_STATE(155) and
    /// GAME_OVER(62) from a conquest win in mission A reach only mission A's peers
    /// — a peer seated on mission B hears nothing and mission B stays Started
    /// (reuses the P178 multi-mission proof pattern).</summary>
    [Fact]
    public void Conquest_win_in_one_mission_does_not_leak_to_another()
    {
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
                mission.PlaceStation(FlagStation(core, sector.Id, a0, 8011, new Vector3(0, 0, -3000)), sector);
                mission.PlaceStation(FlagStation(core, sector.Id, a0, 8098, new Vector3(0, 0, -9000)), sector);
                mission.PlaceStation(FlagStation(core, sector.Id, a1, 8899, new Vector3(0, 0, -1000)), sector);
                placedA = true;
            }
            short sideIdx = seq == 1 ? (short)0 : (short)(seq == 0 ? 0 : 1);   // A0→A/s0, B0→B/s0, A1→A/s1
            var hull = seq == 0
                ? core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmBoard))  // the capturer (mission A, side0)
                : core.HullTypes.Values.OrderBy(h => h.HitPoints).First();
            var ship = new Ship(shipId, hull, new Vector3(0f, 980f, 940f), Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides[sideIdx].Id;
            mission.PlaceShip(ship, sector);
            seq++;
            return (ship, sector);
        };
        server.Boot(ConfigTwoGames());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        var port = server.BoundPort;

        // Peers: 0 = mission A / side0 (capturer), 1 = mission B / side0, 2 = mission A / side1.
        using var a0 = new FltBot(); using var b0 = new FltBot(); using var a1 = new FltBot();
        var bots = new[] { a0, b0, a1 };
        try
        {
            string[] names = { "mA0", "mB0", "mA1" };
            for (int i = 0; i < 3; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            int got = PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(got < 4000, "no assignments");
            foreach (var s in server.Ships) s.Ship.Velocity = Vector3.Zero;

            // The mission A capturer is the boardable side-0 peer on mission A.
            var capturer = server.Ships.First(s => ReferenceEquals(s.Ship.Mission, server.Missions[0])
                                                   && s.Ship.HullType.HasCapability(DataHullTypeIGC.HabmBoard)).Ship;
            var target = server.Missions[0].Sectors[0].StationRuntimes.First(st => st.SideId != capturer.SideId);

            foreach (var b in bots) { b.ResetGameStates(); b.ResetGameOvers(); }
            capturer.Position = new Vector3(0, 0, -1000);   // at the side-1 flag target in mission A

            int seen = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Over, 9000);
            Assert.True(seen < 9000, "mission A did not end by conquest");

            // Mission A ended with the right winner...
            Assert.Equal(MissionStage.Over, server.Missions[0].Stage);
            Assert.Equal(MissionStage.Started, server.Missions[1].Stage);
            Assert.True(a0.GameStates.Count > 0 && a0.GameOvers.Count > 0, "mission A peers must see GAME_STATE + GAME_OVER");

            // ...and mission B's peer heard NOTHING (whole-mission scoped per mission).
            Assert.Empty(b0.GameStates);
            Assert.Empty(b0.GameOvers);

            Console.WriteLine($"P191-MULTI-LIVE: mission A conquest win -> A peers saw {a0.GameStates.Count} GAME_STATE / {a0.GameOvers.Count} GAME_OVER; " +
                              $"mission B peer saw {b0.GameStates.Count} GAME_STATE / {b0.GameOvers.Count}, stage {server.Missions[1].Stage}");
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 955u, DwCookie = 0x955u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9900000000000000000ul, DrmHash = "conquest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A booted 3-bot / 2-side live server with <c>ConquestEnabled</c> on
    /// the mission and three synthetic <c>Flag</c>-capable stations in sector 0
    /// (2 owned by side A, 1 owned by side B — the capture <see cref="Target"/>),
    /// planted by the ShipSpawner. Slots alternate sides round-robin (A,B,A).
    /// <see cref="Ships"/>[i]/<see cref="Bots"/>[i] align to the specs/slots.</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship[] Ships = null!;
        public FltBot[] Bots = null!;
        public Station Target = null!;
        public Sector Sector = null!;
        public StaticCore Core = null!;
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
            SabmCapabilities = DataStationTypeIGC.SabmFlag,   // counts for victory
            MaxArmorHitPoints = 9999f, MaxShieldHitPoints = 0f,
            Radius = 40,
        };
        var data = new DataStationIGC(
            Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: sectorId, SideId: side, StationId: (short)id,
            StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: $"FLAG_{id}");
        return new Station(data, type);
    }

    private sealed record Spec(bool Boardable, Vector3 Pos);

    private static Seated Seat(params Spec[] specs)
    {
        var seat = new Seated();
        var core = Core();
        seat.Core = core;
        seat.Server = new Srv();
        seat.Ships = new Ship[specs.Length];

        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];            // cluster 1051 'Arrokoth' — empty of real stations
            seat.Sector = sector;
            if (!seat.StationsPlaced)
            {
                seat.SideA = mission.Sides[0].Id;
                seat.SideB = mission.Sides[1].Id;
                mission.ConquestEnabled = true;         // P191: opt the conquest win condition in
                // 2 side-A + 1 side-B Flag stations in this sector. With the real
                // map's #1051(side0)+#1052(side1) also Flag, totals = A:3, B:2 (60/40).
                var stA1 = FlagStation(core, sector.Id, seat.SideA, 7911, new Vector3(0, 0, -3000));
                var stA2 = FlagStation(core, sector.Id, seat.SideA, 7912, new Vector3(0, 0, -4000));
                var stB = FlagStation(core, sector.Id, seat.SideB, 7999, StationSpot);   // the capture target
                mission.PlaceStation(stA1, sector);
                mission.PlaceStation(stA2, sector);
                mission.PlaceStation(stB, sector);
                seat.Target = stB;
                seat.StationsPlaced = true;
            }
            var spec = specs[placed];
            DataHullTypeIGC hull = spec.Boardable
                ? core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmBoard))
                : core.HullTypes.Values.OrderBy(h => h.HitPoints).First();
            var ship = new Ship(id, hull, spec.Pos, Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B,A
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
        string[] names = { "p191_a", "p191_b", "p191_team" };
        try
        {
            for (int i = 0; i < n; i++)
            {
                seat.Bots[i] = new FltBot();
                seat.Bots[i].Connect(port, names[i]);
                seat.Bots[i].SendLogonRequest(Request(names[i]));
            }
            PumpUntil(seat.Server, () => seat.Bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(seat.Bots.All(b => b.ShipAssignment is not null), "all bots must be assigned a ship");
            Assert.All(seat.Ships, s => Assert.NotNull(s));
            foreach (var s in seat.Ships) s.Velocity = Vector3.Zero;
            Assert.True(seat.SideA != seat.SideB, "harness needs two distinct sides");
            Assert.Equal(seat.SideA, seat.Ships[0].SideId);   // slot 0 = side A (the boardable capturer)
            Assert.Equal(seat.SideB, seat.Ships[1].SideId);   // slot 1 = side B
            return seat;
        }
        catch
        {
            foreach (var b in seat.Bots) b?.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    private static GameState[] AllGameStates(Seated seat) => seat.Bots.SelectMany(b => b.GameStates).ToArray();
    private static GameOver[] AllGameOvers(Seated seat) => seat.Bots.SelectMany(b => b.GameOvers).ToArray();

    /// <summary>⭐ The full chain: a hostile boardable side-A ship captures the
    /// side-B <c>Flag</c> station → side A's <see cref="Side.ConquestPercent"/>
    /// rises to 80% (≥ the 65% threshold) → a whole-mission GAME_STATE(155) with
    /// the new per-side conquest array reaches EVERY peer, and the mission ends
    /// via the P178 GAME_OVER(62) path (stage Over, winner = side A) — the real
    /// must-fail-if-removed proof of capture → recompute → broadcast → GameOver.</summary>
    [Fact]
    public void Flag_capture_tips_conquest_and_broadcasts_game_state_then_game_over()
    {
        using var seat = Seat(new Spec(true, Far), new Spec(false, Far), new Spec(false, Far));
        var capturer = seat.Ships[0];      // slot 0 = side A, boardable
        var target = seat.Target;          // the side-B Flag station
        var mission = seat.Server.Missions[0];

        foreach (var b in seat.Bots) { b.ResetGameStates(); b.ResetGameOvers(); b.ResetStationCaptures(); }

        // Pre-capture: no station event has fired yet, so the recompute has never
        // run and every side's ConquestPercent is its default 0 (native likewise
        // only recomputes inside the capture/kill handler — fsmission.cpp:3568 —
        // never at mission start). No premature win.
        Assert.Equal((byte)0, mission.Sides[0].ConquestPercent);
        Assert.Equal(MissionStage.Started, mission.Stage);

        // Fly the boardable capturer into the target's dock radius — the next
        // sector tick's dock pass captures it (P126), triggering the recompute.
        capturer.Position = AtTarget;
        PumpUntil(seat.Server, () => mission.Stage == MissionStage.Over, 9000);
        Assert.True(mission.Stage == MissionStage.Over, "conquest win must flip the mission to Over");

        // The captured station really flipped to the capturing side.
        Assert.Equal(seat.SideA, target.SideId);

        // Conquest recompute: side A now owns 4/5 = 80%, side B 20%.
        Assert.Equal((byte)80, mission.Sides[0].ConquestPercent);
        Assert.Equal((byte)20, mission.Sides[1].ConquestPercent);

        // Whole-mission GAME_STATE(155): every peer received it with the new arrays.
        PumpUntil(seat.Server, () => AllGameStates(seat).Length >= seat.Bots.Length, 6000);
        Assert.True(AllGameStates(seat).Length >= seat.Bots.Length,
            $"every peer must receive GAME_STATE; got {AllGameStates(seat).Length}/{seat.Bots.Length}");
        foreach (var gs in AllGameStates(seat))
        {
            Assert.Equal((byte)80, gs.Conquest[seat.SideA]);
            Assert.Equal((byte)20, gs.Conquest[seat.SideB]);
            Assert.All(gs.NFlags, b => Assert.Equal((byte)0, b));       // no flag secured in this conquest scenario → counter stays 0 (P201: no longer dead weight)
            Assert.All(gs.NArtifacts, b => Assert.Equal((byte)0, b));
        }

        // P178 GAME_OVER: every peer, winner = side A, mission scoped.
        PumpUntil(seat.Server, () => AllGameOvers(seat).Length >= seat.Bots.Length, 6000);
        Assert.True(AllGameOvers(seat).Length >= seat.Bots.Length,
            $"every peer must receive GAME_OVER; got {AllGameOvers(seat).Length}/{seat.Bots.Length}");
        foreach (var go in AllGameOvers(seat))
            Assert.Equal(seat.SideA, go.SideWinner);

        Console.WriteLine($"P191-LIVE: capturer {capturer.Id} (side {seat.SideA}) captured flag base {target.StationId}; " +
                          $"conquest {seat.SideA} 60%->80%; GAME_STATE(155) -> {AllGameStates(seat).Length} peers, " +
                          $"GAME_OVER(62) winner={seat.SideA}, stage Over");
    }

    /// <summary>⭐ Regression: with <c>ConquestEnabled</c>/<c>TerritoryEnabled</c>
    /// both false (the default), a real capture produces NO GAME_STATE broadcast,
    /// NO GAME_OVER, and the mission stays Started — existing missions are
    /// unaffected by the new win-condition code.</summary>
    [Fact]
    public void Flags_disabled_capture_broadcasts_nothing_and_does_not_end()
    {
        // Reuse the Seat harness but disable the flags on the mission it created.
        using var seat = Seat(new Spec(true, Far), new Spec(false, Far), new Spec(false, Far));
        seat.Server.Missions[0].ConquestEnabled = false;
        seat.Server.Missions[0].TerritoryEnabled = false;
        var capturer = seat.Ships[0];
        var mission = seat.Server.Missions[0];

        foreach (var b in seat.Bots) { b.ResetGameStates(); b.ResetGameOvers(); b.ResetStationCaptures(); }

        capturer.Position = AtTarget;
        // Let many ticks elapse — the capture would have fired the recompute/broadcast if enabled.
        PumpUntil(seat.Server, () => mission.Stage == MissionStage.Over || seat.Target.SideId == seat.SideA, 9000);
        Assert.Equal(seat.SideA, seat.Target.SideId);              // the capture itself still happens (P126 path)
        Assert.Equal(MissionStage.Started, mission.Stage);         // ...but no win is declared
        Assert.Empty(AllGameStates(seat));                          // no GAME_STATE
        Assert.Empty(AllGameOvers(seat));                           // no GAME_OVER
        Assert.All(mission.Sides, s => Assert.Equal((byte)0, s.ConquestPercent));

        Console.WriteLine("P191-LIVE-DISABLED: capture happened with flags off -> no GAME_STATE, no GAME_OVER, stage Started (default unaffected)");
    }
}
