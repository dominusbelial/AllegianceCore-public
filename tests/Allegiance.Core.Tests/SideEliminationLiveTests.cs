using System.IO;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P455 — live loopback proof of the station-elimination win: destroying or
/// capturing a side's LAST restart-capable station eliminates that side
/// (<see cref="Sector"/> raises the new <c>IIgcSite.SideLostRestartStation</c>
/// seam; the server's <c>OnSideLostRestartStation</c> runs the ported
/// <c>DeactivateSide</c>), which broadcasts <c>SIDE_INACTIVE(68)</c> and the
/// <c>"&lt;Side name&gt; is no more."</c> chat, then the win-by-elimination check
/// (<c>HasPlayers &amp;&amp; Active</c>) crowns the survivor and ends the game via
/// the single shared <c>DeclareGameOver</c> path.
/// <list type="number">
/// <item><b>Kill</b>: a real cross-side missile destroys side B's only restart
/// station (real damage path) → side B is eliminated: its side-0 Bot receives
/// <c>SIDE_INACTIVE(68)</c> for side B with <c>bActive=false</c>, the
/// <c>"Bravo is no more."</c> chat, and <c>GAME_OVER(62)</c> naming side 0 with
/// the native reason ("Alpha won by outlasting all other sides."). Runs against
/// unfixed code this test FAILS (no GAME_OVER) — recorded in the report.</item>
/// <item><b>Negative</b>: a side with two restart stations loses one → no
/// <c>SIDE_INACTIVE</c>, no game-over; the game continues.</item>
/// <item><b>Capture</b>: the last restart station is captured (hostile
/// boardable hull, shields down, real dock-capture path) → the OLD side is
/// eliminated.</item>
/// <item><b>Debug mode</b> (P388): with <c>DebugMode=true</c> and 0 connected
/// players, eliminating the last restart station still deactivates the side and
/// ends the game (the winner test drops <c>HasPlayers</c> and uses
/// <c>Side.Active</c> alone).</item>
/// </list>
/// Construction mirrors <see cref="StationCombatLiveTests"/>' / 
/// <see cref="StationCaptureLiveTests"/>' live harness (2 sides on XXV_simple,
/// synthetic restart-capable stations owned by side B injected via
/// <see cref="Mission.PlaceStation"/>, real missiles / real dock-capture drivers).
/// Ground truth: the live station (<see cref="Station.Dead"/> / side), the
/// mission stage / <see cref="Side.Active"/>, and the wire
/// <see cref="Bot.SideInactiveBroadcasts"/>/<see cref="Bot.Chats"/>/<see cref="Bot.GameOvers"/>.
/// </summary>
[Collection("LiveNetwork")]
public sealed class SideEliminationLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private const short Side1BaseId = 7701;
    private const short Side1SecondId = 7702;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static float Dtm(StaticCore core, byte dmg, byte def) => core.Constants.DamageConstants[dmg][def];

    /// <summary>A (missile, defense-index pair) where the missile's damage type has
    /// strictly positive cells for BOTH armor and shield (so one real hit can
    /// overflow and kill), with real reach to aim a live impact — same driver
    /// <see cref="StationCombatLiveTests"/> uses.</summary>
    private static readonly Lazy<(DataMissileTypeIGC Missile, byte DefArmor, byte DefShield, float Ds, float Da)> Combo = new(() =>
    {
        var core = Core();
        DataMissileTypeIGC? best = null;
        (byte defA, byte defS) = (0, 0);
        (float ds, float da) = (0f, 0f);
        foreach (var m in core.MissileTypes.Values
            .Where(mm => mm.InitialSpeed > 0f && mm.Power > 0f && mm.Lifespan >= 4f && mm.Lifespan <= 8f))
        {
            for (byte s = 0; s < DataConstantsIGC.DefidMax; s++)
                for (byte a = 0; a < DataConstantsIGC.DefidMax; a++)
                {
                    float sCell = Dtm(core, m.DamageType, s);
                    float aCell = Dtm(core, m.DamageType, a);
                    if (sCell > 0f && aCell > 0f && m.InitialSpeed * m.Lifespan >= 90f)
                    {
                        if (best is null || m.Power > best.Value.Power)
                        {
                            best = m; defA = a; defS = s; ds = sCell; da = aCell;
                        }
                        break;
                    }
                }
        }
        if (best is null)
            throw new InvalidOperationException("no (missile, defense-pair) combo with positive cells + reach in representative core");
        return (best!.Value, defA, defS, ds, da);
    });

    private static float FireGap(DataMissileTypeIGC m)
        => MathF.Min(0.55f * m.InitialSpeed * m.Lifespan, 170f);

    private static Cfg Config(int port = 0, bool debug = false) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        LiveSettings = new global::Allegiance.Server.LiveSettingsConfig { DebugMode = debug },
        Games =
        [
            new Gm { Name = "Side Elimination Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static Cfg ConfigThree() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        LiveSettings = new global::Allegiance.Server.LiveSettingsConfig { DebugMode = false },
        Games =
        [
            new Gm { Name = "Side-Only Game Over Test", Map = "XXV_simple.igc", Teams = 3, CivIds = [1, 3, 2], SideNames = ["Alpha", "Bravo", "Charlie"] },
        ],
    };

    private static LogonRequest Request(string name, ulong steam) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 781u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = steam, DrmHash = "sideelim",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A placed-ship spec for seating: whether the hull carries
    /// <c>c_habmBoard</c> (capture driver) and its spawn position.</summary>
    private sealed record Spec(bool Boardable, Vector3 Pos);

    private static readonly Vector3 Far = new(0f, 680f, 740f);
    private static readonly Vector3 AtBase = new(0f, 0f, -1000f);   // side-1 station spot (dist 0 < DockRadius)

    /// <summary>A booted 2-bot / 2-side live server whose side-B (slot 1) owns
    /// exactly the synthetic restart station(s) this harness places in sector 0
    /// (all of the real map's side-B stations are removed first). Slot 0 = side A
    /// (attacker/observer), slot 1 = side B (defender, the side to be eliminated).
    /// <see cref="Side1Bases"/> holds the placed side-B restart stations in order.</summary>
    private sealed class Harness : IDisposable
    {
        public Srv Server = null!;
        public Ship[] Ships = null!;
        public FltBot[] Bots = null!;
        public Sector Sector = null!;
        public Mission Mission = null!;
        public StaticCore Core = null!;
        public short SideA, SideB;
        public List<Station> Side1Bases = new();
        public bool Initialized;

        public void Dispose()
        {
            foreach (var b in Bots) b?.Dispose();
            Server?.Dispose();
        }
    }

    /// <summary>A station type sized/flagged per the test: restart-capable
    /// (c_sabmRestart) by default, with the given armor/shield/defense so one real
    /// Combo missile can kill it (kill tests) or it survives (capture test).</summary>
    private static Station MkSide1Station(Harness h, short id, Vector3 spot, float maxArmor, float maxShield)
    {
        var (_, defArmor, defShield, _, _) = Combo.Value;
        var baseType = h.Core.StationTypes.Values.First();
        var type = baseType with
        {
            ModelName = "P699_SYNTHETIC_ELIMINATION", // synthetic 40-unit base, no artwork bay

            MaxArmorHitPoints = maxArmor,
            MaxShieldHitPoints = maxShield,
            DefenseTypeArmor = defArmor,
            DefenseTypeShield = defShield,
            ShieldRegeneration = 0f,
            ArmorRegeneration = 0f,
            SabmCapabilities = DataStationTypeIGC.SabmRestart | DataStationTypeIGC.SabmLand | DataStationTypeIGC.SabmCapLand,   // P550: restart + landable/capturable (native dock gate needs the land bits)
            Radius = 40,
        };
        var data = new DataStationIGC(
            Position: [spot.X, spot.Y, spot.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: h.Sector.Id, SideId: h.SideB, StationId: id,
            StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: $"SYN_BASE_{id}");
        return new Station(data, type);
    }

    /// <summary>Seat the bots and, on the spawner's first call, strip every real
    /// map station owned by side B so side B owns ONLY the synthetic restart
    /// stations this harness places (the "last restart station" premise). Places
    /// the side-B restart station(s) at the given spots.</summary>
    private static Harness Seat(Vector3[] side1Spots, params Spec[] specs)
    {
        var h = new Harness();
        var core = Core();
        h.Core = core;
        h.Server = new Srv();
        h.Server.SpawnInitialMinersAtBoot = false;   // P382 opt-out: no boot-time NPCs to strand when we strip side-B stations
        h.Ships = new Ship[specs.Length];

        int placed = 0;
        h.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            h.Sector = sector;
            h.Mission = mission;
            h.SideA = mission.Sides[0].Id;
            h.SideB = mission.Sides[1].Id;

            if (!h.Initialized)
            {
                h.Initialized = true;
                // Strip every real map station owned by side B (runtime + static),
                // so side B's restart-station set is exactly what we place below.
                foreach (var sec in mission.Sectors)
                    foreach (var st in sec.StationRuntimes.Where(s => s.SideId == h.SideB).ToList())
                        sec.RemoveStation(st);
                // Place the side-B restart station(s).
                for (int i = 0; i < side1Spots.Length; i++)
                {
                    var st = MkSide1Station(h, (short)(Side1BaseId + i), side1Spots[i], 2000f, 100f);
                    h.Side1Bases.Add(st);
                    mission.PlaceStation(st, sector);
                }
            }

            var spec = specs[placed];
            DataHullTypeIGC hull = spec.Boardable
                ? core.HullTypes.Values.First(x => x.HasCapability(DataHullTypeIGC.HabmBoard))
                : core.HullTypes.Values.OrderBy(x => x.HitPoints).First();
            var ship = new Ship(id, hull, spec.Pos, Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B
            mission.PlaceShip(ship, sector);
            h.Ships[placed] = ship;
            placed++;
            return (ship, sector);
        };
        h.Server.Boot(Config());
        foreach (var __m in h.Server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass ready-up
        var port = h.Server.BoundPort;

        h.Bots = new FltBot[specs.Length];
        string[] names = { "p455_a", "p455_b", "p455_c" };
        try
        {
            for (int i = 0; i < specs.Length; i++)
            {
                h.Bots[i] = new FltBot();
                h.Bots[i].Connect(port, names[i]);
                h.Bots[i].SendLogonRequest(Request(names[i], 77400000000000000ul + (ulong)i));
                Assert.True(PumpUntil(h.Server, () => h.Bots[i].ShipAssignment is not null, 4000) < 4000,
                    $"peer {names[i]} must seat in spawn slot {i}");
                Assert.Equal(h.Ships[i].Id, h.Bots[i].ShipAssignment!.Ship.ShipId);
            }
            PumpUntil(h.Server, () => h.Bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(h.Bots.All(b => b.ShipAssignment is not null), "all bots must be assigned a ship");
            Assert.All(h.Ships, s => Assert.NotNull(s));
            foreach (var s in h.Ships) s.Velocity = Vector3.Zero;
            Assert.True(h.SideA != h.SideB, "harness needs two distinct sides");
            Assert.Equal(h.SideA, h.Ships[0].SideId);   // slot 0 = side A
            Assert.Equal(h.SideB, h.Ships[1].SideId);   // slot 1 = side B
            Assert.NotEmpty(h.Side1Bases);
            return h;
        }
        catch
        {
            foreach (var b in h.Bots) b?.Dispose();
            h.Server.Dispose();
            throw;
        }
    }

    /// <summary>A booted 3-bot / 3-side live server (Alpha/Bravo/Charlie) whose
    /// side-C (slot 2) owns exactly the synthetic restart station this harness
    /// places in sector 0 (all real side-C map stations are removed first), so
    /// destroying it eliminates ONLY side C while sides A & B play on. Bots 0/1/2
    /// are seated on sides 0/1/2 respectively.</summary>
    private sealed class Harness3 : IDisposable
    {
        public Srv Server = null!;
        public Ship[] Ships = null!;
        public FltBot[] Bots = null!;
        public Sector Sector = null!;
        public Mission Mission = null!;
        public StaticCore Core = null!;
        public short SideA, SideB, SideC;
        public Station? CharlieBase;
        public bool Initialized;

        public void Dispose()
        {
            foreach (var b in Bots) b?.Dispose();
            Server?.Dispose();
        }
    }

    /// <summary>A restart-capable station owned by side C (the 3-side analogue of
    /// <see cref="MkSide1Station"/>), sized so one real Combo missile can kill it.</summary>
    private static Station MkCharlieStation(Harness3 h, short id, Vector3 spot, float maxArmor, float maxShield)
    {
        var (_, defArmor, defShield, _, _) = Combo.Value;
        var baseType = h.Core.StationTypes.Values.First();
        var type = baseType with
        {
            ModelName = "P699_SYNTHETIC_ELIMINATION", // synthetic 40-unit base, no artwork bay

            MaxArmorHitPoints = maxArmor,
            MaxShieldHitPoints = maxShield,
            DefenseTypeArmor = defArmor,
            DefenseTypeShield = defShield,
            ShieldRegeneration = 0f,
            ArmorRegeneration = 0f,
            SabmCapabilities = DataStationTypeIGC.SabmRestart | DataStationTypeIGC.SabmLand | DataStationTypeIGC.SabmCapLand,   // P550: restart + landable/capturable (native dock gate needs the land bits)
            Radius = 40,
        };
        var data = new DataStationIGC(
            Position: [spot.X, spot.Y, spot.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: h.Sector.Id, SideId: h.SideC, StationId: id,
            StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: $"SYN_BASE_{id}");
        return new Station(data, type);
    }

    /// <summary>Seat 3 bots on 3 sides and, on the spawner's first call, strip
    /// every real map station owned by side C so side C owns ONLY the synthetic
    /// restart station placed below (the \"last restart station\" premise).</summary>
    private static Harness3 SeatThree(Vector3 charlieSpot)
    {
        var h = new Harness3();
        var core = Core();
        h.Core = core;
        h.Server = new Srv();
        h.Server.SpawnInitialMinersAtBoot = false;   // P382 opt-out
        h.Ships = new Ship[3];

        int placed = 0;
        h.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            h.Sector = sector;
            h.Mission = mission;
            h.SideA = mission.Sides[0].Id;
            h.SideB = mission.Sides[1].Id;
            h.SideC = mission.Sides[2].Id;

            if (!h.Initialized)
            {
                h.Initialized = true;
                // Strip every real map station owned by side C (runtime + static),
                // so side C's restart-station set is exactly what we place below.
                foreach (var sec in mission.Sectors)
                    foreach (var st in sec.StationRuntimes.Where(s => s.SideId == h.SideC).ToList())
                        sec.RemoveStation(st);
                var stC = MkCharlieStation(h, Side1BaseId, charlieSpot, 2000f, 100f);
                h.CharlieBase = stC;
                mission.PlaceStation(stC, sector);
            }

            var hull = core.HullTypes.Values.OrderBy(x => x.HitPoints).First();
            var ship = new Ship(id, hull, Far, Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides[placed % 3].Id;   // bots 0/1/2 -> sides 0/1/2
            mission.PlaceShip(ship, sector);
            h.Ships[placed] = ship;
            placed++;
            return (ship, sector);
        };
        h.Server.Boot(ConfigThree());
        foreach (var __m in h.Server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass ready-up
        var port = h.Server.BoundPort;

        h.Bots = new FltBot[3];
        string[] names = { "p455b_a", "p455b_b", "p455b_c" };
        try
        {
            for (int i = 0; i < 3; i++)
            {
                h.Bots[i] = new FltBot();
                h.Bots[i].Connect(port, names[i]);
                h.Bots[i].SendLogonRequest(Request(names[i], 7790000000000000000ul + (ulong)i));
                // Bind the named peer to its spawn-slot side before another
                // receive thread can race this join.
                Assert.True(PumpUntil(h.Server, () => h.Bots[i].ShipAssignment is not null, 4000) < 4000,
                    $"peer {names[i]} must seat in spawn slot {i}");
                Assert.Equal(h.Ships[i].Id, h.Bots[i].ShipAssignment!.Ship.ShipId);
            }
            PumpUntil(h.Server, () => h.Bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(h.Bots.All(b => b.ShipAssignment is not null), "all bots must be assigned a ship");
            Assert.All(h.Ships, s => Assert.NotNull(s));
            foreach (var s in h.Ships) s.Velocity = Vector3.Zero;
            Assert.True(h.SideA != h.SideB && h.SideB != h.SideC && h.SideA != h.SideC, "harness needs three distinct sides");
            Assert.Equal(h.SideA, h.Ships[0].SideId);
            Assert.Equal(h.SideB, h.Ships[1].SideId);
            Assert.Equal(h.SideC, h.Ships[2].SideId);
            Assert.NotNull(h.CharlieBase);
            return h;
        }
        catch
        {
            foreach (var b in h.Bots) b?.Dispose();
            h.Server.Dispose();
            throw;
        }
    }

    private static void AimAndFire(Harness h, Ship shooter, Station target)
    {
        var (missile, _, _, _, _) = Combo.Value;
        shooter.Position = target.Center + new Vector3(0f, 0f, FireGap(missile));
        shooter.FireMissile(missile, shooter.Mission!.LastUpdate);
    }

    /// <summary>⭐ A real cross-side missile destroys side B's ONLY restart station
    /// (the real damage path → the P455 seam → DeactivateSide) and side A's Bot
    /// receives SIDE_INACTIVE(68) for side B (bActive=false), the "Bravo is no
    /// more." chat, and GAME_OVER(62) naming side 0 with the native reason.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Destroying_last_restart_station_eliminates_side_and_declares_winner()
    {
        Require.Artwork();
        using var h = Seat([AtBase], new Spec(false, Far), new Spec(false, Far));
        var shooter = h.Ships[0];            // side A
        var bas = h.Side1Bases[0];           // side B's only restart station
        var aBot = h.Bots[0];
        // P869: retain the pre-retirement state; the live mission registry is cleared.
        var retiredSide = h.Mission.Sides.First(s => s.Id == h.SideB);

        foreach (var b in h.Bots) { b.ResetLeadershipSignals(); b.ResetGameOvers(); b.ResetChatReceived(); }

        Assert.True(bas.Type.HasCapability(DataStationTypeIGC.SabmRestart), "test base must be restart-capable");
        Assert.Equal(h.SideB, bas.SideId);
        Assert.True(h.Mission.Sides.First(s => s.Id == h.SideB).Active);

        AimAndFire(h, shooter, bas);
        PumpUntil(h.Server, () => bas.Dead, 12000);
        Assert.True(bas.Dead, "lethal cross-side hit must destroy the station");
        Assert.DoesNotContain(bas, h.Sector.StationRuntimes);   // killed station is gone — the elimination walk truly sees none left

        // Side B is now eliminated.
        Assert.True(retiredSide.Active == false, "losing side must be deactivated");

        // Side A's bot hears it all: SIDE_INACTIVE, the chat, and GAME_OVER naming side A.
        PumpUntil(h.Server, () => aBot.GameOvers.Count > 0, 8000);
        Assert.True(aBot.SideInactiveBroadcasts.Any(s => s.SideId == h.SideB && !s.BActive),
            "side-A bot must receive SIDE_INACTIVE(68) for the eliminated side");
        Assert.True(aBot.Chats.Any(c => c.Message == "Bravo is no more."), "side-A bot must receive the elimination chat");
        var go = Assert.Single(aBot.GameOvers);
        Assert.Equal(h.SideA, go.SideWinner);
        Assert.Contains("won by outlasting all other sides", go.GameoverMessage);
        Assert.Equal(MissionStage.Over, h.Mission.Stage);
        Console.WriteLine($"P455-LIVE-KILL: destroyed side-{h.SideB} last restart station {bas.StationId} -> side deactivated, " +
                          $"SIDE_INACTIVE(68) + chat + GAME_OVER winner side {go.SideWinner} \"{go.GameoverMessage}\"");
    }

    /// <summary>⭐ P455b — THREE sides, one player each, all still active: destroy
    /// side C's (Charlie's) ONLY restart station. Side C is eliminated but the
    /// game is NOT over (sides A & B remain). The side-only game-over goes ONLY
    /// to side C's bot — <c>GAME_OVER(62)</c> with the native reason
    /// <c>"Your side has been eliminated."</c> (iSideWinner = NA, no global
    /// winner) plus <c>GAME_OVER_PLAYERS(63)</c> — while side A & side B bots get
    /// <c>SIDE_INACTIVE(68)</c> for side C but NO game-over, and the mission stays
    /// <see cref="MissionStage.Started"/>.</summary>
    [Trait("Requires", "Artwork")]
    [Trait("Lane", "LiveTiming")]   // P647 quarantine: flaky live-wire side-only game-over receipt even in isolation
    [SkippableFact]
    public void Eliminating_side2_sends_side_only_gameover_to_it_alone()
    {
        Require.Artwork();
        using var h = SeatThree(AtBase);
        var shooter = h.Ships[0];          // side A (Alpha) fires the kill
        var bas = h.CharlieBase!;          // side C's (Charlie) only restart station
        var cBot = h.Bots[2];              // side C — the eliminated side's player
        var aBot = h.Bots[0];              // side A — survivor
        var bBot = h.Bots[1];              // side B — survivor

        foreach (var b in h.Bots)
        {
            b.ResetLeadershipSignals();
            b.ResetGameOvers();
            b.ResetGameOverPlayerBroadcasts();
            b.ResetChatReceived();
        }

        Assert.True(bas.Type.HasCapability(DataStationTypeIGC.SabmRestart), "test base must be restart-capable");
        Assert.Equal(h.SideC, bas.SideId);
        Assert.True(h.Mission.Sides.First(s => s.Id == h.SideC).Active);

        // Fire a real cross-side Combo missile at side C's only restart station
        // (same driver AimAndFire uses; the 2-side harness type is irrelevant).
        var (missile, _, _, _, _) = Combo.Value;
        shooter.Position = bas.Center + new Vector3(0f, 0f, FireGap(missile));
        shooter.FireMissile(missile, shooter.Mission!.LastUpdate);
        PumpUntil(h.Server, () => bas.Dead, 12000);
        Assert.True(bas.Dead, "lethal cross-side hit must destroy side C's only restart station");

        // Side C is eliminated; sides A & B are still alive → the game is NOT over.
        Assert.True(h.Mission.Sides.First(s => s.Id == h.SideC).Active == false, "losing side C must be deactivated");
        Assert.True(h.Mission.Sides.First(s => s.Id == h.SideA).Active);
        Assert.True(h.Mission.Sides.First(s => s.Id == h.SideB).Active);
        Assert.Equal(MissionStage.Started, h.Mission.Stage);   // not Over, no rotation

        // The eliminated side's own bot gets the side-only GAME_OVER + GAME_OVER_PLAYERS.
        PumpUntil(h.Server,
            () => cBot.GameOvers.Count > 0 && cBot.GameOverPlayerBroadcasts.Count > 0
                && aBot.SideInactiveBroadcasts.Any(s => s.SideId == h.SideC && !s.BActive)
                && bBot.SideInactiveBroadcasts.Any(s => s.SideId == h.SideC && !s.BActive),
            20000);
        var go = Assert.Single(cBot.GameOvers);
        Assert.Equal(GameOver.NA, go.SideWinner);                       // no global winner at side-only elimination
        Assert.Equal("Your side has been eliminated.", go.GameoverMessage);  // native QueueGameoverMessage default
        Assert.NotEmpty(cBot.GameOverPlayerBroadcasts);
        Assert.Contains(cBot.GameOverPlayerBroadcasts,
            gp => gp.Entries.Any(e => e.SideId == h.SideC));            // roster names the eliminated side's player

        // Survivors hear SIDE_INACTIVE for side C but see NO game-over; they keep playing.
        Assert.True(aBot.SideInactiveBroadcasts.Any(s => s.SideId == h.SideC && !s.BActive),
            "side-A bot must receive SIDE_INACTIVE for the eliminated side");
        Assert.True(bBot.SideInactiveBroadcasts.Any(s => s.SideId == h.SideC && !s.BActive),
            "side-B bot must receive SIDE_INACTIVE for the eliminated side");
        Assert.Empty(aBot.GameOvers);                                   // side A NOT shown a game-over
        Assert.Empty(bBot.GameOvers);                                   // side B NOT shown a game-over
        Assert.Equal(MissionStage.Started, h.Mission.Stage);            // mission still running for the survivors

        Console.WriteLine($"P455B-LIVE: destroyed side-{h.SideC} only restart station -> side-{h.SideC} bot got " +
                          $"side-only GAME_OVER [msg={go.GameoverMessage}] (iSideWinner={go.SideWinner}) + GAME_OVER_PLAYERS; " +
                          $"sides {h.SideA}/{h.SideB} got SIDE_INACTIVE only, stage still Started");
    }

    /// <summary>⭐ Negative: a side with TWO restart stations loses one — it still
    /// owns the other, so it is NOT eliminated: no SIDE_INACTIVE, no game-over;
    /// the game continues (side stays active, mission stays Started).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Side_with_two_restart_stations_survives_losing_one()
    {
        Require.Artwork();
        using var h = Seat([AtBase, new Vector3(500f, 0f, -1000f)], new Spec(false, Far), new Spec(false, Far));
        var shooter = h.Ships[0];            // side A
        var first = h.Side1Bases[0];         // the one we destroy
        var second = h.Side1Bases[1];        // the survivor
        var aBot = h.Bots[0];

        foreach (var b in h.Bots) { b.ResetLeadershipSignals(); b.ResetGameOvers(); b.ResetChatReceived(); }

        Assert.True(first.Type.HasCapability(DataStationTypeIGC.SabmRestart));
        Assert.True(second.Type.HasCapability(DataStationTypeIGC.SabmRestart));

        AimAndFire(h, shooter, first);
        PumpUntil(h.Server, () => first.Dead, 12000);
        Assert.True(first.Dead, "first station must be destroyed");
        // The surviving second restart station is still owned by side B.
        Assert.Contains(second, h.Sector.StationRuntimes);
        Assert.Equal(h.SideB, second.SideId);

        // No elimination: side B stays active, no SIDE_INACTIVE, no game-over.
        Assert.True(h.Mission.Sides.First(s => s.Id == h.SideB).Active, "side B must NOT be deactivated");
        Assert.Equal(MissionStage.Started, h.Mission.Stage);
        Assert.DoesNotContain(aBot.SideInactiveBroadcasts, s => s.SideId == h.SideB);
        Assert.Empty(aBot.GameOvers);
        Console.WriteLine("P455-LIVE-NEG: side with 2 restart stations lost 1 -> still owns a restart station, not eliminated, game continues");
    }

    /// <summary>⭐ The capture path: side B's ONLY restart station is CAPTURED (real
    /// hostile boardable-hull dock with shields down), so the OLD side is
    /// eliminated — SIDE_INACTIVE + chat + GAME_OVER naming side A.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Capturing_last_restart_station_eliminates_old_side()
    {
        Require.Artwork();
        using var h = Seat([AtBase], new Spec(true, Far), new Spec(false, Far));
        var capturer = h.Ships[0];           // side A, boardable hull
        var bas = h.Side1Bases[0];           // side B's only restart station
        var aBot = h.Bots[0];
        // P869: retain the pre-retirement state; the live mission registry is cleared.
        var retiredSide = h.Mission.Sides.First(s => s.Id == h.SideB);

        foreach (var b in h.Bots) { b.ResetLeadershipSignals(); b.ResetGameOvers(); b.ResetChatReceived(); }

        Assert.True(capturer.HullType.HasCapability(DataHullTypeIGC.HabmBoard), "capturer must be boardable");
        Assert.True(bas.Type.HasCapability(DataStationTypeIGC.SabmRestart));
        Assert.Equal(h.SideB, bas.SideId);

        // P450: capture requires shields DOWN — drain the shield to 0 (real damage path).
        // P475: the station's shield capacity is GA-scaled (side B = civ 3 Ga'Taraan,
        // MaxShieldStation base 1.1), so drain the EFFECTIVE max to guarantee depletion.
        // P649b: NULL launchers still use the loaded matrix (stationIGC.cpp:160-171).
        // Type 0's shield cell is zero here; use Combo's positive cell and scale
        // the amount so absorbed = amount * dtmShield / effectiveMaxShield = 1.
        var (missile, _, _, dtmShield, _) = Combo.Value;
        bas.ReceiveDamage(missile.DamageType, bas.EffectiveMaxShield() / dtmShield,
            0.0, Vector3.Zero, Vector3.Zero, launcher: null);
        Assert.Equal(0f, bas.ShieldFraction, 3);

        // Fly the hostile boardable capturer into the dock radius → real capture.
        capturer.Position = AtBase;
        PumpUntil(h.Server, () => bas.SideId == h.SideA, 9000);
        Assert.True(bas.SideId == h.SideA, "hostile boardable ship must capture the station");
        // The captured station now belongs to the captor — it no longer counts for side B.
        Assert.NotEqual(h.SideB, bas.SideId);

        // Side B is eliminated (lost its last restart station to the capture).
        Assert.True(retiredSide.Active == false, "captured side must be deactivated");
        PumpUntil(h.Server, () => aBot.GameOvers.Count > 0, 8000);
        Assert.True(aBot.SideInactiveBroadcasts.Any(s => s.SideId == h.SideB && !s.BActive),
            "side-A bot must receive SIDE_INACTIVE(68) for the captured-away side");
        Assert.True(aBot.Chats.Any(c => c.Message == "Bravo is no more."), "side-A bot must receive the elimination chat");
        var go = Assert.Single(aBot.GameOvers);
        Assert.Equal(h.SideA, go.SideWinner);
        Assert.Contains("won by outlasting all other sides", go.GameoverMessage);
        Assert.Equal(MissionStage.Over, h.Mission.Stage);
        Console.WriteLine($"P455-LIVE-CAPTURE: side-{h.SideB} last restart station captured -> old side deactivated, GAME_OVER winner side {go.SideWinner}");
    }

    /// <summary>⭐ Debug mode (P388): with DebugMode=true and 0 connected players,
    /// eliminating side B's only restart station still deactivates the side and
    /// ENDS the game — the winner test drops HasPlayers and uses Side.Active
    /// alone, so the 0-player arena crowns side A. Driven by a synthetic (non-peer)
    /// side-A ship firing a real missile.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Debug_mode_elimination_still_declares_winner_with_zero_players()
    {
        Require.Artwork();
        var h = new Harness();
        var core = Core();
        h.Core = core;
        using var debugServer = new Srv();
        h.Server = debugServer;
        h.Server.SpawnInitialMinersAtBoot = false;
        h.Server.Boot(Config(debug: true));
        var mission = h.Server.Missions[0];
        foreach (var __m in h.Server.Missions) __m.Stage = MissionStage.Started;
        h.Mission = mission;
        h.Sector = mission.Sectors[0];
        h.SideA = mission.Sides[0].Id;
        h.SideB = mission.Sides[1].Id;
        // P869: retain the pre-retirement state; the live mission registry is cleared.
        var retiredSide = mission.Sides.First(s => s.Id == h.SideB);

        // 0 connected players is the point of this test — connect none.

        // Strip side-B map stations, place one synthetic restart station for side B.
        foreach (var sec in mission.Sectors)
            foreach (var st in sec.StationRuntimes.Where(s => s.SideId == h.SideB).ToList())
                sec.RemoveStation(st);
        var bas = MkSide1Station(h, Side1BaseId, AtBase, 2000f, 100f);
        h.Side1Bases.Add(bas);
        mission.PlaceStation(bas, h.Sector);

        Assert.Equal(h.SideB, bas.SideId);
        Assert.True(mission.Sides.First(s => s.Id == h.SideB).Active);
        Assert.Equal(MissionStage.Started, mission.Stage);

        // A synthetic (non-peer) side-A ship fires a real missile at side B's only
        // restart station — the real damage path, with no players anywhere.
        var shooter = new Ship(7777, core.HullTypes.Values.OrderBy(x => x.HitPoints).First(),
            Vector3.Zero, Vector3.Zero, Orientation.Identity);
        shooter.SideId = h.SideA;
        mission.PlaceShip(shooter, h.Sector);

        var log = new StringWriter();
        var orig = Console.Out;
        try
        {
            Console.SetOut(log);
            AimAndFire(h, shooter, bas);
            PumpUntil(h.Server, () => bas.Dead, 12000);
            Assert.True(bas.Dead, "lethal hit must destroy the debug station");
            // Pump a little more so DeclareGameOver (which runs synchronously in the
            // same TickSim) has definitely been reached and logged.
            PumpUntil(h.Server, () => mission.Stage == MissionStage.Over, 4000);
        }
        finally
        {
            Console.SetOut(orig);
        }

        // Elimination happened and the mission ENDED (the P388 skip must NOT swallow it).
        Assert.True(retiredSide.Active == false, "debug eliminated side must be deactivated");
        Assert.Equal(MissionStage.Over, mission.Stage);
        // The winner test dropped HasPlayers → side A wins, NOT a draw (NA).
        string captured = log.ToString();
        Assert.Contains("[gameover]", captured);
        Assert.Contains($"iSideWinner={h.SideA}", captured);
        Assert.DoesNotContain("iSideWinner=NA(draw)", captured);
        Console.WriteLine($"P455-LIVE-DEBUG: 0 players + debugMode -> elimination still ended the game, winner side {h.SideA} (not a draw)");

    }
}
