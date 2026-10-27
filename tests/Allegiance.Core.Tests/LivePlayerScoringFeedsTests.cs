using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using PlayerScore = global::Allegiance.Server.PlayerScore;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P583 (native player scoring, Stage 2) — live proof that the port's existing
/// server EVENT paths really feed the new counters (the "one test per event
/// feeding its counter" requirement): a rescue award via
/// <c>PerformRescue</c> → <c>Rescues</c>+1, a base kill via
/// <c>BroadcastStationDestroyed</c> → <c>BaseKills</c>+1, a base capture via
/// <c>BroadcastStationCapture</c> → <c>BaseCaptures</c>+1, a class kill via
/// <c>BroadcastKillShip</c> → <c>MinerKills</c>+1 (a mining victim), and an
/// ejection via <c>BroadcastKillShip</c>'s <c>!bKilled</c> branch →
/// <c>Ejections</c>+1. Each test drives the real private server method on the
/// sim thread (the established "testing private members" reflection pattern
/// from <see cref="LiveCombatScoringTests"/>), and FAILS if that feed line is
/// removed.
/// </summary>
[Collection("LiveNetwork")]
public class LivePlayerScoringFeedsTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon)> Fight = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var hull in core.HullTypes.Values
                     .Where(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod))
                     .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                     .OrderBy(h => h.HullId))
        {
            foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                         .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                         .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                         .OrderBy(w => w.EnergyPerShot))
            {
                if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Power > 0f)
                    return (hull, p);
            }
        }
        throw new InvalidOperationException("no fightable non-lifepod hull+weapon in the representative core");
    });

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Test Mission", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest SampleRequest(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 926u, DwCookie = 0x926u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 9270000000000000000ul, DrmHash = "pscore583",
    };

    private const float TargetZ = -180f;

    /// <summary>Seat two peers on DISTINCT sides and return their ships.</summary>
    private static (Srv server, Ship a, Ship b, FltBot ab, FltBot bb) SeatTwo()
    {
        var server = new Srv();
        var placed = false;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (hull, _) = Fight.Value;
            Vector3 pos = !placed ? new Vector3(0, 0, 0) : new Vector3(0, 0, TargetZ);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, mission.Sectors[0]);
            ship.SideId = (short)(placed ? 1 : 0);
            placed = true;
            return (ship, mission.Sectors[0]);
        };
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        var a = new FltBot(); var b = new FltBot();
        a.Connect(server.BoundPort, "ACE"); b.Connect(server.BoundPort, "BEE");
        a.SendLogonRequest(SampleRequest("ACE")); b.SendLogonRequest(SampleRequest("BEE"));
        int got = PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "both peers must seat");
        return (server,
            server.Ships.First(s => s.Ship.Id == a.ShipAssignment!.Ship.ShipId).Ship,
            server.Ships.First(s => s.Ship.Id == b.ShipAssignment!.Ship.ShipId).Ship,
            a, b);
    }

    private static PlayerScore ScoreOf(Srv server, Ship ship) =>
        server.Ships.First(s => ReferenceEquals(s.Ship, ship)).PlayerScore;

    private static readonly MethodInfo? PerformRescue = typeof(Srv).GetMethod(
        "PerformRescue", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? BroadcastStationDestroyed = typeof(Srv).GetMethod(
        "BroadcastStationDestroyed", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? BroadcastStationCapture = typeof(Srv).GetMethod(
        "BroadcastStationCapture", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? BroadcastKillShip = typeof(Srv).GetMethod(
        "BroadcastKillShip", BindingFlags.Instance | BindingFlags.NonPublic);

    private static void FireKill(Srv server, Ship victim, Ship? launcher, float amount)
    {
        BroadcastKillShip!.Invoke(server, new object?[]
        {
            1000.0, victim, launcher, amount, new Vector3(0, 0, 0), new Vector3(0, 0, 0),
        });
    }

    /// <summary>⭐ The rescue event feeds the rescuer's <c>Rescues</c> counter
    /// (native <c>AddRescue</c>, FedSrv.CPP:13039): the port's
    /// <c>PerformRescue</c> awards +1 to a peer rescuer.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rescue_event_awards_the_rescuer_a_rescue()
    {
        Require.Artwork();
        var seat = SeatTwo();
        using var server = seat.server;
        using var ab = seat.ab;
        using var bb = seat.bb;
        Ship rescuer = seat.a;
        var mission = rescuer.Mission!;
        var sector = mission.Sectors[0];

        var rescuee = new Ship((short)880, Fight.Value.hull, rescuer.Position + new Vector3(5, 0, 0), Vector3.Zero, Orientation.Identity)
        { SideId = rescuer.SideId };
        mission.PlaceShip(rescuee, sector);

        PerformRescue!.Invoke(server, new object?[] { rescuee, rescuer });

        Assert.Equal(1, ScoreOf(server, rescuer).Rescues);
    }

    /// <summary>⭐ The base-kill event feeds the credited launcher's
    /// <c>BaseKills</c> counter (native <c>KillBase(true)</c>,
    /// FedSrv.CPP:12252): <c>BroadcastStationDestroyed</c> credits a peer
    /// launcher.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Base_kill_event_credits_the_launcher_a_base_kill()
    {
        Require.Artwork();
        var seat = SeatTwo();
        using var server = seat.server;
        using var ab = seat.ab;
        using var bb = seat.bb;
        var station = seat.a.Mission!.Sectors.SelectMany(s => s.StationRuntimes).First();

        BroadcastStationDestroyed!.Invoke(server, new object?[] { station, seat.a });

        Assert.Equal(1f, ScoreOf(server, seat.a).BaseKills);
    }

    /// <summary>⭐ The side-level base-kill counter credits ANY launcher with a
    /// side (native <c>psideLauncher-&gt;AddBaseKill()</c>,
    /// FedSrv.CPP:12269-12273) — not only <c>Ship</c> launchers. A probe (a
    /// non-Ship model) destroying a station still increments its side's
    /// <see cref="Side.BaseKills"/>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Base_kill_side_counter_credits_a_non_ship_launcher_with_a_side()
    {
        Require.Artwork();
        var seat = SeatTwo();
        using var server = seat.server;
        using var ab = seat.ab;
        using var bb = seat.bb;
        var mission = seat.a.Mission!;
        var station = mission.Sectors.SelectMany(s => s.StationRuntimes).First();
        var side = mission.Sides.Single(sd => sd.Id == seat.a.SideId);
        Assert.Equal((short)0, side.BaseKills);

        // A probe (non-Ship launcher) with the side's id destroys the station.
        var probe = new Probe((short)890, mission.Sectors[0],
            mission.Core.ProbeTypes.Values.First(), Vector3.Zero, 0.0, seat.a.SideId, seat.a);
        BroadcastStationDestroyed!.Invoke(server, new object?[] { station, probe });

        Assert.Equal((short)1, side.BaseKills);
        // The player-score arm (KillBase(true)) is still OT_ship-only, so a
        // probe launcher never touches the peer's player score.
        Assert.Equal(0f, ScoreOf(server, seat.a).BaseKills);
    }

    /// <summary>⭐ The base-capture event feeds the capturing pilot's
    /// <c>BaseCaptures</c> counter (native <c>CaptureBase(true)</c>,
    /// fsship.cpp:600): <c>BroadcastStationCapture</c> credits the capturing
    /// peer.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Base_capture_event_credits_the_capturing_pilot()
    {
        Require.Artwork();
        var seat = SeatTwo();
        using var server = seat.server;
        using var ab = seat.ab;
        using var bb = seat.bb;
        var station = seat.a.Mission!.Sectors.SelectMany(s => s.StationRuntimes).First();

        BroadcastStationCapture!.Invoke(server, new object?[] { seat.a, station });

        Assert.Equal(1f, ScoreOf(server, seat.a).BaseCaptures);
    }

    /// <summary>⭐ A kill of a MINING hull feeds <c>MinerKills</c> (native
    /// <c>KillShip</c> routes <c>c_ptMiner</c> → <c>m_cMinerKills</c>,
    /// igc.h:5680-5683): <c>BroadcastKillShip</c> buckets the peer killer's
    /// credit by the victim's class.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Killing_a_mining_hull_feeds_the_miner_kill_counter()
    {
        Require.Artwork();
        var seat = SeatTwo();
        using var server = seat.server;
        using var ab = seat.ab;
        using var bb = seat.bb;
        Ship killer = seat.a;
        var mission = killer.Mission!;
        var sector = killer.Sector!;

        var minerHull = RepresentativeCore().HullTypes.Values
            .First(h => h.HasCapability(DataHullTypeIGC.HabmMiner));
        short otherSide = (short)(1 - killer.SideId!.Value);   // flip 0↔1 → cross-side credit
        var victim = new Ship((short)881, minerHull, killer.Position + new Vector3(0, 0, -60f), Vector3.Zero, Orientation.Identity)
        { SideId = otherSide };
        mission.PlaceShip(victim, sector);

        // P846 — the victim-class credit is now native's damage SHARE
        // (FedSrv.CPP:11841-11861), so the kill needs a real damage-track bucket.
        // A ship that dies in the game always has one (ReceiveDamage populated
        // it); apply a non-lethal real hit first, exactly as native's track does.
        var core = RepresentativeCore();
        byte dmg = 0;
        while (dmg < 20 && core.Constants.DamageConstants[dmg][minerHull.DefenseType] <= 0.01f) dmg++;
        float dtm = core.Constants.DamageConstants[dmg][minerHull.DefenseType];
        float hit = 0.25f * victim.EffectiveMaxHitPoints() / dtm;
        victim.ReceiveDamage(dmg, hit, mission.LastUpdate, victim.Position, killer.Position, killer);

        FireKill(server, victim, killer, 10f);

        Assert.Equal(1f, ScoreOf(server, killer).MinerKills);   // sole dealer → 100% share
        Assert.Equal(1, ScoreOf(server, killer).Kills);
    }

    /// <summary>⭐ An ejection (a non-killed ship becoming a lifepod) feeds the
    /// victim's <c>Ejections</c> counter (native <c>AddEjection</c>,
    /// FedSrv.CPP:11919-11921) — NOT a death.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Ejecting_a_peer_counts_an_ejection_not_a_death()
    {
        Require.Artwork();
        var seat = SeatTwo();
        using var server = seat.server;
        using var ab = seat.ab;
        using var bb = seat.bb;
        Ship killer = seat.a;
        Ship victim = seat.b;   // a real peer on the other (valid) side → eject succeeds

        FireKill(server, victim, killer, 1.0f);

        Assert.Equal(1, ScoreOf(server, victim).Ejections);
        Assert.Equal(0, ScoreOf(server, victim).Deaths);
    }
}
