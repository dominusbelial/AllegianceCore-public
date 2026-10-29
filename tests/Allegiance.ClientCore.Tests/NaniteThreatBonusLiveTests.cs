using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P299 — LIVE wire proof of the nanite gun's 2× repair-credit bonus for
/// bomber/HTT-class targets (<c>c_habmThreatToStation</c>). Two real in-process
/// servers on <c>XC02.igc</c>, each with a real nan-gunned shooter (P277
/// harness) and a friendly target damaged to the SAME fraction: once with a
/// real Dreg BOMBER hull (<c>dn_fadrshbmb</c>, which carries the bit) and once
/// with a real fighter/scout hull that does NOT carry it. Both are repaired
/// back to full over the real wire by the real ER Nanite; the shooter's accrued
/// <see cref="Ship.Repair"/> (the launcher-side "fraction of a hull repaired"
/// stat native <c>m_repair</c> represents) is read from the live server ship.
/// Because each target is repaired by the SAME fraction of its own hull, the
/// bomber earns 2× the fighter's credit — the measured before/after the task
/// demands, in real server state driven by real fired rounds.
/// </summary>
[Collection("LiveNetwork")]
public class NaniteThreatBonusLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    // The same nan-mountable shooter hull + ER Nanite picker P277 uses.
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC pt)> Nan = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var hull in core.HullTypes.Values
                     .Where(h => h.MaxEnergy > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                     .OrderBy(h => h.HullId))
        {
            foreach (var w in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                         .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                         .Where(w => w.EnergyPerShot <= hull.MaxEnergy)
                         .OrderBy(w => w.ProjectileTypeId))
            {
                if (core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var pt) && pt.Power < 0f)
                    return (hull, w, pt);
            }
        }
        throw new InvalidOperationException("no mountable nan weapon in XC02.igc");
    });

    /// <summary>A real target hull from XC02.igc: a bomber (carries the threat
    /// bit) for <paramref name="threat"/>=true, otherwise a hull WITHOUT the bit.
    /// Selected by bit (the same model recurs with different capability sets),
    /// preferring a bomber for the threat case and a fighter for the non-threat.</summary>
    private static DataHullTypeIGC TargetHull(bool threat)
    {
        var core = RepresentativeCore();
        const short bit = DataHullTypeIGC.HabmThreatToStation;
        var matches = core.HullTypes.Values
            .Where(h => ((h.HabmCapabilities & bit) != 0) == threat)
            .OrderBy(h => h.HullId)
            .ToList();
        foreach (var h in matches)
            if (threat ? h.ModelName.Contains("bmb", StringComparison.OrdinalIgnoreCase)
                       : h.ModelName.Contains("fig", StringComparison.OrdinalIgnoreCase))
                return h;
        return matches[0];
    }

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "XC02.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("XC02.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "XC02.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: preserve the pre-flag 30 s auto-seat (these peers never send POSITIONREQ)
        Games =
        [
            new Gm { Name = "Nan Threat", Map = "XXV_simple.igc", Teams = 1, CivIds = [1], SideNames = ["Alpha"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 928u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 99200000000000000ul, DrmHash = "nantreat",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private const float TargetZ = -100f;

    // Spawn the shooter (nan gun) at origin and the target at -100 with the
    // chosen hull, both on side 0 (single-team mission → friendly).
    private static void InjectSpawn(Srv server, DataHullTypeIGC targetHull)
    {
        var placedShooter = false;
        var field = typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("ShipSpawner field not found on Srv");
        Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner = (core, mission, id) =>
        {
            var (hull, weapon, _) = Nan.Value;
            Vector3 pos = !placedShooter ? Vector3.Zero : new Vector3(0f, 0f, TargetZ);
            var ship = new Ship(id, !placedShooter ? hull : targetHull, pos, Vector3.Zero, Orientation.Identity) { SideId = 0 };
            mission.PlaceShip(ship, mission.Sectors[0]);
            if (!placedShooter)
            {
                ship.Energy = hull.MaxEnergy * 1000f;   // sustain long fire
                ship.MountWeapon(weapon);               // the ER Nanite mount
            }
            placedShooter = true;
            return (ship, mission.Sectors[0]);
        };
        field.SetValue(server, spawner);
    }

    private static Ship FindServerShip(Srv server, Vector3 position)
    {
        foreach (var s in server.Missions[0].Sectors)
            foreach (var m in s.Models)
                if (m is Ship sh && (sh.Position - position).Length() < 1e-3f)
                    return sh;
        throw new InvalidOperationException($"no server ship at {position}");
    }

    /// <summary>Locate a live server ship by its id — robust to the ship drifting
    /// from its spawn point. P621: the real convex-hull ship-ship collision
    /// (<c>Separate</c>'s hull contact normal) lets a friendly target thrusting
    /// into the shooter resolve legitimate elastic collisions, nudging the
    /// shooter a hair off its exact spawn origin; the old exact-position lookup
    /// is an over-fragile implementation detail (the test's purpose is nan
    /// repair-credit, not pinning the shooter to origin).</summary>
    private static Ship FindServerShip(Srv server, short id)
    {
        foreach (var s in server.Missions[0].Sectors)
            foreach (var m in s.Models)
                if (m is Ship sh && sh.Id == id)
                    return sh;
        throw new InvalidOperationException($"no live server ship with id {id}");
    }

    private static void DamageHullTo(Ship ship, float fraction)
    {
        var f = typeof(Ship).GetField("_fraction", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Ship._fraction field not found");
        f.SetValue(ship, fraction);
    }

    /// <summary>
    /// ⭐ LIVE PROOF. Boot a real server, repair a friendly target back to full
    /// with the real ER Nanite, and return the shooter's accrued
    /// <see cref="Ship.Repair"/>. The shooter fires continuously; once the
    /// target's hull reaches full no further credit accrues, so the returned
    /// value is the total credit for restoring (<c>startFraction</c> → 1.0) of
    /// that target's hull — <c>(1−startFraction)×2</c> for a bomber, ×1 for a
    /// non-threat hull.
    /// </summary>
    private static (float credit, int ticks, string model) RunScenario(bool threat, float startFraction)
    {
        var targetHull = TargetHull(threat);
        using var server = new Srv();
        InjectSpawn(server, targetHull);
        server.Boot(Config());
        ushort port = server.BoundPort;

        using var shooter = new ConnectionSession();
        using var target = new ConnectionSession();
        shooter.Connect(port, "nan_shooter");
        target.Connect(port, "nan_target");
        shooter.SendLogonRequest(Request("nan_shooter"));
        target.SendLogonRequest(Request("nan_target"));

        PumpUntil(server, () => shooter.World.MyShipId is not null && target.World.MyShipId is not null, 4000);
        Assert.NotNull(shooter.World.MyShipId);
        Assert.NotNull(target.World.MyShipId);

        var shPos = shooter.World.Ships[shooter.World.MyShipId!.Value].Latest!.Position;
        var tgPos = target.World.Ships[target.World.MyShipId!.Value].Latest!.Position;
        bool shooterAtOrigin = shPos.Z > tgPos.Z;
        var gunner = shooterAtOrigin ? shooter : target;
        var prey = shooterAtOrigin ? target : shooter;
        short shooterId = gunner.World.MyShipId!.Value;
        short targetId = prey.World.MyShipId!.Value;
        Assert.NotEqual(shooterId, targetId);

        shooter.SendPlayerReady(fReady: true, shipId: shooterId);
        target.SendPlayerReady(fReady: true, shipId: targetId);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, $"mission never reached Started (stage={server.Missions[0].Stage})");

        var targetShip = FindServerShip(server, targetId);
        DamageHullTo(targetShip, startFraction);
        prey.CommandControls(new ControlData(0f, 0f, 0f, 0.35f), ShipControlState.ForwardButton);

        int sawDamage = PumpUntil(server,
            () => prey.World.Ships.TryGetValue(targetId, out var e)
                  && e.Latest is not null && e.Latest.HullFraction <= startFraction + 0.003f, 1600);
        Assert.True(sawDamage < 1600, "target's own client never observed its damaged hull");

        gunner.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);

        int healed = PumpUntil(server,
            () => prey.World.Ships.TryGetValue(targetId, out var e)
                  && e.Latest is not null && e.Latest.HullFraction >= 0.993f, 12000);
        Assert.True(healed < 12000,
            $"target never reached full over the wire (latest={(prey.World.Ships.TryGetValue(targetId, out var se) ? se.Latest?.HullFraction.ToString("F3") : "none")})");

        var shooterShip = FindServerShip(server, shooterId);
        float credit = shooterShip.Repair;
        Console.WriteLine($"LIVE-NAN-CREDIT[{targetHull.ModelName} thr={threat}]: shooter.Repair = {credit:F4} after {healed} ticks (start {startFraction})");
        return (credit, healed, targetHull.ModelName);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Threat_target_earns_2x_repair_credit_over_wire()
    {
        Require.Artwork();
        const float start = 0.5f;   // same fraction of each hull repaired → to full
        var bomber = RunScenario(threat: true, start);
        var fighter = RunScenario(threat: false, start);

        // Each target was repaired by (1-start)=0.5 of its OWN hull. Native's
        // credit is "fraction of a hull repaired" (shipIGC.h:1882-1884): the
        // fighter earns ≈0.5, the bomber ≈1.0 — a 2:1 ratio for the same work.
        float expectedFighter = 1f * (1f - start);
        float expectedBomber = 2f * (1f - start);
        float ratio = bomber.credit / fighter.credit;
        Console.WriteLine($"LIVE-NAN-CREDIT-RATIO: bomber {bomber.credit:F3} / fighter {fighter.credit:F3} = {ratio:F3} (expect ≈2)");

        Assert.True(bomber.credit > expectedBomber * 0.8f && bomber.credit < expectedBomber * 1.25f,
            $"bomber credit {bomber.credit:F3} not near {expectedBomber:F3}");
        Assert.True(fighter.credit > expectedFighter * 0.8f && fighter.credit < expectedFighter * 1.25f,
            $"fighter credit {fighter.credit:F3} not near {expectedFighter:F3}");
        Assert.True(ratio > 1.6f && ratio < 2.5f,
            $"bomber/fighter credit ratio {ratio:F3} not ≈ 2");
    }
}
