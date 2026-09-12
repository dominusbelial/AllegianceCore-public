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
/// P162 — the Wingman combat-AI foundation: the plain <c>c_cidAttack</c>
/// default order (native <c>PickDefaultOrder</c>'s <c>c_ptWingman</c> case,
/// <c>Igc/shipIGC.h:2436-2455</c>). An idle <see cref="PilotTypeWingman"/> NPC
/// picks the nearest enemy ship in its own sector
/// (<see cref="Ship.FindNearestEnemyShip"/>, "enemy" = a different
/// <see cref="Ship.SideId"/>), commits via <see cref="Ship.AttackTarget"/>, and
/// <see cref="Ship.UpdateWingmanCombat"/> then steers it to within weapon range
/// and — when it is facing the target — sets the <c>AllWeapons</c>
/// <see cref="Ship.StateM"/> bit that the already-built
/// <see cref="Weapon.Update"/>/<see cref="Weapon.FireWeapon"/> mechanic fires
/// on. This live loopback test proves the FULL loop, not just a StateM bit
/// flip: decision → pursuit → the pre-existing weapon mechanic engaging for
/// real, evidenced by the enemy ship's <see cref="Ship.Fraction"/> dropping
/// from 1.0 (a real projectile collision dealing damage, the same damage
/// observability <c>LiveCombatWireTests</c> exercises to a kill).
/// <list type="bullet">
/// <item><b>Primary leg</b> — an idle real Wingman NPC (a genuine Wingman
/// drone type from PCore001, <c>PilotType == 2</c>) with a real weapon mounted
/// picks the nearest enemy ship in the sector, closes distance, holds the
/// trigger when in range and facing, and actually damages the enemy.</item>
/// <item><b>Decision-gate leg</b> — a Wingman with <b>no</b> enemy in its
/// sector stands by (no <see cref="Ship.AttackTarget"/>, no movement, no
/// firing) — the faithful no-op matching native's
/// <c>SetCommand(c_cmdAccepted, NULL, c_cidNone)</c> when <c>FindTarget</c>
/// finds nothing (<c>shipIGC.h:2458-2459</c>).</item>
/// </list>
/// Harness mirrors P160/P161 (booted in-process server + one seated bot, no
/// Windows VM); the mission advances with the server's own deterministic
/// <see cref="Srv.TickSim"/> (one 0.05 s chunk each).
/// </summary>
[Collection("LiveNetwork")]
public sealed class NpcWingmanAttackLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

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
            new Gm { Name = "P162 Wingman Attack Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 557u, DwCookie = 0x557u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 55200000000000005ul, DrmHash = "p162wingman",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // A hull that can actually fight (energy+ammo and a fixed hardpoint at
    // mount 0) paired with the cheapest dispersion-0 weapon that does real
    // damage (projectile Power > 0) — the exact fightable loadout
    // LiveCombatWireTests selects, so the Wingman's mounted weapon genuinely
    // fires real damaging rounds. (Dispersion-0: no aim scatter; Power > 0:
    // the round actually hurts.)
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon)> Fight = new(() =>
    {
        var core = Core();
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

    /// <summary>Shared boot: one seeded player seats a ship into the mission
    /// (giving the test its Mission/Sector handles); the real Wingman drone
    /// type id (PilotType==2) is captured from the core. When
    /// <paramref name="peerInSeparateSector"/> is true the peer is parked in a
    /// different sector (for the no-enemy negative control); otherwise it sits
    /// in a far corner of the Wingman's sector (primary leg), still far enough
    /// that the test's dedicated enemy is the nearest. Returns (server,
    /// mission, sector, npcSide, peerSide, wingmanDroneTypeId).</summary>
    private static (Srv Server, Mission Mission, Sector Sector, short NpcSide, short PeerSide, short WingmanDroneId)
        Boot(bool peerInSeparateSector = false)
    {
        var server = new Srv();
        Mission? mission = null;
        Sector? sector = null;
        short npcSide = 0;
        short peerSide = 0;
        short wingDroneId = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            var core = m.Core;
            peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;   // the OTHER side — no players

            var wing = core.DroneTypes.First(kv => kv.Value.PilotType == Ship.PilotTypeWingman);
            wingDroneId = wing.Key;

            // The seated peer player ship. For the primary leg it is parked in
            // a far corner of the Wingman's sector so the nearest-enemy search
            // unambiguously favours the test's dedicated enemy (much closer);
            // for the negative control it is moved to the LAST sector so the
            // Wingman's own sector genuinely contains no enemy-side ship.
            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var peerSector = peerInSeparateSector ? m.Sectors[m.Sectors.Count - 1] : m.Sectors[0];
            var ship = new Ship(id, hull, new Vector3(980f, 632f, 621f), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(ship, peerSector);
            return (ship, peerSector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        // P178: keep the bot SEATED (no `using` → not disposed at Boot return)
        // so the mission stays Started — the server's win-by-elimination now
        // (correctly) ends a mission when its last player leaves. The server
        // closes this socket at teardown.
        var bot = new FltBot();
        bot.Connect(server.BoundPort, "p162_wingman");
        bot.SendLogonRequest(Request("p162_wingman"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);

        Assert.NotNull(mission); Assert.NotNull(sector);
        Assert.True(wingDroneId != 0, "core must expose a Wingman pilot-type drone");
        return (server, mission!, sector!, npcSide, peerSide, wingDroneId);
    }

    private static float Dist(Vector3 a, Vector3 b) => (b - a).Length();

    /// <summary>⭐ Primary leg: a real idle Wingman NPC with a mounted weapon
    /// picks the nearest enemy ship in the sector, closes distance, holds the
    /// trigger when in range and facing, and REALLY damages the enemy — the
    /// full decision → pursuit → fire loop, evidenced by the enemy's
    /// <see cref="Ship.Fraction"/> dropping below 1.0 (a real projectile
    /// collision), not merely a <see cref="Ship.StateM"/> bit flip.</summary>
    [Fact]
    public void Wingman_engages_nearest_enemy_closes_distance_and_deals_damage()
    {
        var (srv, mission, sector, npcSide, peerSide, wingDroneId) = Boot();
        using (srv)
        {
            var core = mission.Core;

            // ── The enemy: a plain ship on the OTHER side (peerSide), parked
            //    and inert. Give it the longest hull so its collision radius
            //    (0.5 · HullType.Length, Sector.cs:1128) is large — an easy,
            //    unambiguous target, which makes the live hit robust.
            var enemyHull = core.HullTypes.Values.OrderByDescending(h => h.Length).First();
            Vector3 ep = new(640f, 548f, 507f);
            var enemy = new Ship((short)5001, enemyHull, ep, Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            mission.PlaceShip(enemy, sector);

            // ── The Wingman: spawn a real Wingman drone type, then arm it with
            //    the fightable loadout so it can actually fire. d0 is well
            //    beyond any reasonable engagement standoff, so it must TRAVEL to
            //    reach fire range.
            Vector3 wp = new(220f, 544f, 498f);
            var wingman = srv.SpawnNpcShip(mission, sector, npcSide, wp, wingDroneId);
            Assert.Equal(Ship.PilotTypeWingman, wingman.PilotType);   // gate premise: really a Wingman

            var (fhull, weapon) = Fight.Value;
            wingman.ChangeHull(fhull);
            wingman.Energy = fhull.MaxEnergy * 100f;                   // sustain fire: nothing recharges energy yet (slice)
            wingman.Ammo = 10000;                                      // ample magazine
            wingman.MountWeapon(weapon);
            float fireRange = core.ProjectileTypes.TryGetValue(weapon.ProjectileTypeId, out var pt)
                ? pt.Speed * pt.Lifespan
                : 0f;
            Console.WriteLine($"P162-RANGE: mounted weapon projectile range = {fireRange:F1} (standoff caps at 350)");

            float d0 = Dist(wp, ep);
            Assert.True(d0 > 400f, $"wingman must spawn well beyond engagement standoff: d0={d0:F1}");

            // ── Phase 1: decision — the idle Wingman picks the nearest enemy.
            Pump(srv, () => wingman.AttackTarget is not null, 700);
            Assert.Same(enemy, wingman.AttackTarget);
            Assert.Null(wingman.MiningAsteroidTarget);                 // a Wingman never touches the miner's slot
            Assert.Null(wingman.LayerAsteroidTarget);                  // nor the Layer's
            Console.WriteLine($"P162-DECIDE: Wingman {wingman.Id} (drone {wingDroneId}) chose enemy {enemy.Id} (side {enemy.SideId})");

            // ── Phases 2-3: pursuit + fire. Tick until the enemy takes real
            //    damage (Fraction < 1.0 — a real hit) or the budget expires,
            //    sampling whether the fire-selection bit ever got set and the
            //    closest approach reached.
            bool sawTrigger = false;
            float minDist = d0;
            int ticks = 0;
            const int MaxTicks = 20000;
            while (ticks < MaxTicks && enemy.Fraction >= 1.0f)
            {
                srv.TickSim();
                ticks++;
                Thread.Sleep(1);
                if ((wingman.StateM & ShipControlState.AllWeapons) != 0) sawTrigger = true;
                float d = Dist(wingman.Position, ep);
                if (d < minDist) minDist = d;
            }
            float dNow = Dist(wingman.Position, ep);

            Console.WriteLine($"P162-FIRE: ticks={ticks}, sawTrigger={sawTrigger}, enemy.Fraction={enemy.Fraction:R}, d0={d0:F1} → dNow={dNow:F1} (min {minDist:F1})");

            Assert.True(sawTrigger, "the Wingman must hold the trigger (AllWeapons StateM bit) at least once");
            Assert.True(enemy.Fraction < 1.0f,
                $"the enemy must take REAL damage (fraction dropped below 1.0): fraction={enemy.Fraction:R} after {ticks} ticks");
            Assert.True(minDist < d0 - 30f,
                $"the Wingman must close distance toward the enemy: d0={d0:F1} → min {minDist:F1}");
            Assert.True(dNow < d0, $"final distance must be under the start: {d0:F1} → {dNow:F1}");
            Assert.Same(enemy, wingman.AttackTarget);                  // still engaged (target not cleared prematurely)
        }
    }

    /// <summary>⭐ Decision-gate leg (negative control): a real idle Wingman
    /// NPC with NO enemy-side ship in its sector stands by — no
    /// <see cref="Ship.AttackTarget"/>, no movement, no firing. The peer (the
    /// only other-side ship) is parked in a different sector, and a same-side
    /// friend sits nearby, so the nearest-enemy search over the Wingman's own
    /// sector finds nothing — proving the attack behavior only engages when an
    /// enemy is actually present (the faithful no-op of native
    /// <c>shipIGC.h:2458-2459</c>), and that a same-side ship is never
    /// mistaken for an enemy.</summary>
    [Fact]
    public void Wingman_with_no_enemy_in_sector_stands_by()
    {
        // The peer (the only other-side ship) is parked in the LAST sector, so
        // the Wingman's own sector contains no enemy-side ship at all.
        var (srv, mission, sector, npcSide, _, wingDroneId) = Boot(peerInSeparateSector: true);
        using (srv)
        {
            var core = mission.Core;

            // A same-side friend near the Wingman: even though it is the
            // nearest ship in the sector, it is NOT an enemy (same SideId) and
            // must never be targeted.
            var (fhull, weapon) = Fight.Value;
            var friend = new Ship((short)5002, fhull, new Vector3(360f, 518f, 509f), Vector3.Zero, Orientation.Identity) { SideId = npcSide };
            mission.PlaceShip(friend, sector);

            Vector3 wp = new(250f, 527f, 513f);
            var wingman = srv.SpawnNpcShip(mission, sector, npcSide, wp, wingDroneId);
            Assert.Equal(Ship.PilotTypeWingman, wingman.PilotType);
            wingman.ChangeHull(fhull);
            wingman.Energy = fhull.MaxEnergy * 100f;
            wingman.Ammo = 10000;
            wingman.MountWeapon(weapon);

            // Tick well past the idle decision latency. The nearest ship in the
            // sector is the same-side friend (not an enemy); the only enemy-side
            // ship (the peer) is in another sector, invisible to this same-
            // sector-only search. So no target can ever be picked.
            Pump(srv, () => wingman.AttackTarget is not null, 1000);

            Assert.Null(wingman.AttackTarget);            // no enemy in this sector → no attack target
            float travelled = (wp - wingman.Position).Length();
            Assert.True(travelled < 1f, $"wingman with no enemy must not move: travelled {travelled:F3}");
            Assert.Equal(0, wingman.StateM & ShipControlState.AllWeapons);   // never held the trigger
            Console.WriteLine($"P162-STANDBY: Wingman {wingman.Id} with no enemy in sector stood by (travelled {travelled:F3}, no target)");
        }
    }
}
