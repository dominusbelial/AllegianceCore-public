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

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P277 — LIVE wire proof of the nanite gun. A real in-process server (booted
/// on a nan-bearing static core, <c>XC02.igc</c> — PCore001, which the other
/// live tests use, carries no repair weapon), two real
/// <see cref="ConnectionSession"/>s on the SAME side, one real weapon-mounted
/// fighter firing the decoded ER Nanite at a friendly that was first damaged.
/// The observed signal is the TARGET'S OWN <see cref="ClientWorld"/> entity:
/// its <see cref="ServerShipUpdate.HullFraction"/> must climb back up across
/// real <c>SINGLE_SHIP_UPDATE</c> wire messages — proof the repair is applied
/// server-side and reaches the client as real data, not a client-side visual.
///
/// Native identity of the weapon: a normal <c>DataWeaponTypeIGC</c> whose
/// projectile has negative Power — the exact discriminant native's own AI uses
/// to recognise a repair mount (<c>shipIGC.cpp:2608</c>). The repair is
/// <c>ReceiveDamage</c>'s <c>amount &lt; 0</c> branch (<c>shipIGC.cpp:1244-1267</c>);
/// the ship friendly-fire gate now admits a negative-power round to a same-side
/// hull (<c>shipIGC.cpp:1222-1228</c> gates only <c>amount &gt;= 0</c>).
/// </summary>
[Collection("LiveNetwork")]
public class NaniteRepairLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    // A hull that can carry the nan (fixed hardpoint + enough energy for its
    // 30 energy-per-shot) paired with a real decoded ER Nanite (negative-power
    // projectile) from the same core the server boots — resolvable by the
    // server's real weapon-firing path.
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
        Games =
        [
            new Gm { Name = "Nan Repair", Map = "XXV_simple.igc", Teams = 1, CivIds = [1], SideNames = ["Alpha"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 927u, DwCookie = 0x927u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 99100000000000000ul, DrmHash = "nanrepair",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // Shooter nose-to-nose with the target: shooter at origin (identity
    // forward −z), target down −z so the nan round heading −z flies straight
    // into it; the target also faces −z and cruises AWAY along the same line,
    // which keeps it on the flight path (still reparably hit) while its
    // position changes every tick so its OWN SingleShipUpdate (with the real
    // hull fraction) keeps flowing to its session.
    private const float TargetZ = -100f;

    private static void InjectNanSpawn(Srv server)
    {
        var placedShooter = false;
        var field = typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("ShipSpawner field not found on Srv");
        Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner = (core, mission, id) =>
        {
            var (hull, weapon, _) = Nan.Value;
            Vector3 pos = !placedShooter ? Vector3.Zero : new Vector3(0f, 0f, TargetZ);
            // Both peers sit on the SAME side (0) so this is the friendly-repair
            // scenario — and a single-team mission auto-starts once the one
            // occupied side is ready.
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = 0 };
            mission.PlaceShip(ship, mission.Sectors[0]);   // sets ship.Mission/Sector (MountWeapon needs it)
            if (!placedShooter)
            {
                ship.Energy = hull.MaxEnergy * 100f;       // sustain fire: nothing recharges energy yet (slice)
                ship.MountWeapon(weapon);                  // the ER Nanite mount
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

    private static void DamageHullTo(Ship ship, float fraction)
    {
        var f = typeof(Ship).GetField("_fraction", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Ship._fraction field not found");
        f.SetValue(ship, fraction);
    }

    /// <summary>
    /// ⭐ LIVE PROOF. Damage a friendly ship's hull, fire the real ER Nanite at
    /// it from another ship, and watch its hull fraction climb back up in the
    /// <b>real wire data</b> the target's own session receives. The nan is an
    /// energy weapon (no ammo), so the shooter sustains fire with a large
    /// energy pool; the weapon fires along the −z heading straight into the
    /// receding friendly. Steps: both peers assign → ready → Started; the
    /// target is driven to 0.5 hull and made to cruise (so its updates stream);
    /// we confirm the client SEES ~0.5; the shooter then commands AllWeapons;
    /// we pump until the target's own ClientWorld hull fraction exceeds 0.5 by a
    /// real margin. Must-fail: disabling the sector friendly-fire-gate fix
    /// leaves the fraction pinned at 0.5 forever.
    /// </summary>
    [Fact]
    public void NanRound_RepairsFriendlyHull_InRealWireData()
    {
        using var server = new Srv();
        InjectNanSpawn(server);
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

        // Resolve who sits at the origin (the nan shooter) by spawn position.
        var shPos = shooter.World.Ships[shooter.World.MyShipId!.Value].Latest!.Position;
        var tgPos = target.World.Ships[target.World.MyShipId!.Value].Latest!.Position;
        bool shooterAtOrigin = shPos.Z > tgPos.Z;             // shooter z=0 > target z=-100
        var gunner = shooterAtOrigin ? shooter : target;
        var prey = shooterAtOrigin ? target : shooter;
        short shooterId = gunner.World.MyShipId!.Value;
        short targetId = prey.World.MyShipId!.Value;
        Assert.NotEqual(shooterId, targetId);

        // Ready both (single team — the one occupied side), watch the mission
        // cross into Started through the real 15s countdown.
        shooter.SendPlayerReady(fReady: true, shipId: shooterId);
        target.SendPlayerReady(fReady: true, shipId: targetId);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, $"mission never reached Started (stage={server.Missions[0].Stage})");

        // Damage the friendly target's hull to 0.5 server-side, then make it
        // cruise so its own SingleShipUpdate (with the real hull fraction)
        // keeps streaming to its session.
        var targetShip = FindServerShip(server, new Vector3(0f, 0f, TargetZ));
        DamageHullTo(targetShip, 0.5f);
        prey.CommandControls(new ControlData(0f, 0f, 0f, 0.35f), ShipControlState.ForwardButton);  // cruise → dirty → updates flow

        // Confirm the client has actually SEEN the damaged 0.5 hull over the wire.
        int sawDamage = PumpUntil(server,
            () => prey.World.Ships.TryGetValue(targetId, out var e)
                  && e.Latest is not null && e.Latest.HullFraction <= 0.52f, 1600);
        Assert.True(sawDamage < 1600,
            $"target's own client never observed its damaged hull (latest={(prey.World.Ships.TryGetValue(targetId, out var se0) ? se0.Latest?.HullFraction.ToString() : "none")})");
        float damaged = prey.World.Ships[targetId].Latest!.HullFraction;
        Assert.InRange(damaged, 0.0f, 0.52f);

        // The shooter fires the nan (selection bit only), stays at rest.
        gunner.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);

        // Drive until the target's OWN client hull fraction climbs back well
        // above the damaged value — real repair in real wire data.
        int healed = PumpUntil(server,
            () => prey.World.Ships.TryGetValue(targetId, out var e)
                  && e.Latest is not null && e.Latest.HullFraction > damaged + 0.05f, 2400);
        Assert.True(healed < 2400,
            $"friendly hull never climbed back up over the wire (damaged={damaged:F3}, latest={(prey.World.Ships.TryGetValue(targetId, out var se) ? se.Latest?.HullFraction.ToString("F3") : "none")})");

        float repaired = prey.World.Ships[targetId].Latest!.HullFraction;
        Console.WriteLine($"LIVE-NAN: friendly {targetId} hull {damaged:F3} → {repaired:F3} over the wire ({healed} ticks); shooter {shooterId} unharmed");
    }
}
