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

// xUnit collection definitions do not cross assemblies; this is the local
// copy of the LiveNetwork collection (see JoinFlowLiveTests).
[Collection("LiveNetwork")]
public class KillShipLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    // A hull that can actually fight (energy+ammo and a fixed hardpoint at
    // mount 0) paired with the cheapest dispersion-0 weapon doing real damage
    // (projectile Power > 0) — the exact same selection LiveCombatWireTests
    // uses, so a real projectile really can kill the target.
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon)> Fight = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var hull in core.HullTypes.Values
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
        throw new InvalidOperationException("no fightable hull+weapon in the representative core");
    });

    private static StaticCore RepresentativeCore()
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
            new Gm { Name = "Kill Tracking", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 612u, DwCookie = 0xD1Eu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 81400000000000000ul, DrmHash = "killtrack",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Shooter nose-to-nose with the target at origin: shooter at
    /// (0,0,0), target down −z so a dispersion-0 projectile heading −z flies
    /// straight into it. Same geometry as LiveCombatWireTests.</summary>
    private const float TargetZ = -180f;

    /// <summary>Reach the server's documented spawn-customisation seam. The
    /// reference <c>LiveCombatWireTests</c> sets <c>Server.ShipSpawner</c>
    /// directly, but the read-only <c>Allegiance.Server</c> assembly declares
    /// <c>InternalsVisibleTo</c> only for its own test assembly and its tools
    /// (not for <c>Allegiance.ClientCore.Tests</c>) — so that hook is invisible
    /// here without modifying the read-only repo. Rather than alter anything
    /// under <c>AllegianceCore/</c>, we reach the same <c>ShipSpawner</c> field
    /// via reflection. This is not mocking or bypassing behavior: it sets the
    /// exact delegate the server itself calls in <c>AssignShip</c>, so the kill
    /// still resolves through the server's real collision/damage path and a
    /// real KILL_SHIP(105) still crosses the wire.</summary>
    private static void InjectFightSpawn(Srv server, Vector3 shooterPos, Vector3 targetPos)
    {
        var placedShooter = false;
        var field = typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("ShipSpawner field not found on Srv");
        Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner = (core, mission, id) =>
        {
            var (hull, weapon) = Fight.Value;
            Vector3 pos = !placedShooter ? shooterPos : targetPos;
            // P180/P188 + P107: the SHOOTER carries a real side so its side is
            // occupied and the test readying it drives the mission's real
            // ready-up auto-start (a null-sided ship occupies no side, so it
            // can't participate in IsAllOccupiedSidesReady). The TARGET stays
            // SIDE-LESS on purpose: a side-bearing target that takes a fatal
            // hit would EJECT to a drifting lifepod (P107) instead of dying —
            // and an ejected lifepod is dead-in-models (fraction stays 0 and it
            // is dropped from the sector collision list), so it can never take
            // the second, killing hit. A null-sided victim makes EjectSurvivor
            // bail ("cannot eject, treating as death"), so the first real kill
            // resolves straight to a genuine KILL_SHIP(105) — the behavior this
            // pre-P107 test was written to assert. The friendly-fire gate does
            // not block a null-sided target (side != shooter's side), so the
            // shooter's projectile still deals damage.
            short? sideId = !placedShooter ? mission.Sides[0].Id : null;
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = sideId };
            mission.PlaceShip(ship, mission.Sectors[0]);   // sets ship.Mission/Sector (MountWeapon needs it)
            if (!placedShooter)
            {
                ship.Energy = hull.MaxEnergy * 100f;       // sustain fire: nothing recharges energy yet (slice)
                ship.MountWeapon(weapon);                  // the mount the server's default spawn now performs
            }
            placedShooter = true;
            return (ship, mission.Sectors[0]);
        };
        field.SetValue(server, spawner);
    }

    /// <summary>
    /// Step 4 — death tracking, live. A real in-process server, two real
    /// <see cref="ConnectionSession"/>s, a real weapon-mounted fighter shooting
    /// a real target until the server resolves the kill and broadcasts a real
    /// <c>FM_S_KILL_SHIP(105)</c>. The VICTIM is our observer: its own ship id
    /// is necessarily present in its own <see cref="ClientWorld"/> (it is the
    /// SET_CLUSTER-assigned <c>IsMine</c> ship), so when its own death crosses
    /// the wire the entity's <c>IsDead</c> must flip <b>true</b> while the
    /// entity itself stays in <c>Ships</c> — the exact Step 1 behavior driven
    /// end to end. (Side-scoping: a same-side pairing isn't required because we
    /// observe the victim's OWN ship, which is always in its own table; the
    /// shooter is the one who needs to see it to credit the kill — we assert
    /// on the victim's own death so the cross-side teammate-export question is
    /// irrelevant to this test.)
    /// </summary>
    [Fact]
    public void VictimShipEntity_MarkedDead_WhenRealKillCrossesTheWire()
    {
        using var server = new Srv();
        InjectFightSpawn(server, new Vector3(0, 0, 0), new Vector3(0, 0, TargetZ));
        server.Boot(Config());
        ushort port = server.BoundPort;

        using var shooter = new ConnectionSession();
        using var victim = new ConnectionSession();
        shooter.Connect(port, "death_shooter");
        victim.Connect(port, "death_victim");
        shooter.SendLogonRequest(Request("death_shooter"));
        victim.SendLogonRequest(Request("death_victim"));

        PumpUntil(server, () => shooter.World.MyShipId is not null && victim.World.MyShipId is not null, 4000);
        Assert.NotNull(shooter.World.MyShipId);
        Assert.NotNull(victim.World.MyShipId);

        // The ShipSpawner seam gives the first-spawned ship (origin, weapon
        // mounted) to whichever logon the server drains first; resolve who is
        // the shooter by spawn position, not connection order — the same
        // approach the reference LiveCombatWireTests takes.
        var shPos = shooter.World.Ships[shooter.World.MyShipId!.Value].Latest!.Position;
        var vicPos = victim.World.Ships[victim.World.MyShipId!.Value].Latest!.Position;
        bool shooterAtOrigin = shPos.Z > vicPos.Z;
        var gunner = shooterAtOrigin ? shooter : victim;
        var prey = shooterAtOrigin ? victim : shooter;
        short shooterId = gunner.World.MyShipId!.Value;
        short victimId = prey.World.MyShipId!.Value;
        Assert.NotEqual(shooterId, victimId);

        // P180/P188 gate: combat never integrates while NotStarted — a weapon
        // can't fire and a projectile can't resolve a kill until the mission is
        // Started. Ready BOTH peers (whatever their sides, every occupied side
        // must be aggregate-ready), then watch the server-side Mission.Stage
        // (Server.Missions[0]) cross into Started through the 15s countdown.
        shooter.SendPlayerReady(fReady: true, shipId: shooterId);
        victim.SendPlayerReady(fReady: true, shipId: victimId);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, $"mission never reached Started after two-peer ready-up (stage={server.Missions[0].Stage})");

        // Shooter commands AllWeapons (selection bits only) and stays at rest;
        // target idles. The C++ weapon fires along the ship's heading (−z).
        gunner.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);
        prey.CommandControls(ControlData.Neutral, 0);

        // Drive the real server until the victim session's OWN entity flips
        // IsDead — the point of the test. (Its ship was alive at spawn.)
        Assert.False(prey.World.Ships[victimId].IsDead, "victim spawns alive");
        int ticks = PumpUntil(server, () => prey.World.Ships.TryGetValue(victimId, out var e) && e.IsDead, 4800);
        Assert.True(ticks < 4800, $"victim never died within {ticks} ticks");

        // Post-death: entity still present (not removed), flagged dead, and the
        // kill event recorded too.
        Assert.True(prey.World.Ships.ContainsKey(victimId), "victim entity must remain in the table after death");
        Assert.True(prey.World.Ships[victimId].IsDead);
        Assert.Contains(prey.World.Events.Kills, k => k.ShipId == victimId);

        Console.WriteLine($"LIVE-KILL: victim {victimId} (gunner {shooterId}) marked dead after {ticks} ticks; entity kept in Ships[{victimId}]");
    }
}
