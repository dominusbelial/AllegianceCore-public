using System.Reflection;
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
/// P171 — Wingman combat AI, Stage 4: dynamic range maintenance (simplified).
/// Native's real Wingman maintains range by the target's CLOSING SPEED, not a
/// fixed standoff: within the in-the-heat band it backs off when closing too
/// fast (<c>relativeSpeed &gt; 20 → backwardButtonIGC</c>), presses in when
/// falling behind (<c>relativeSpeed &lt; −20 → forwardButtonIGC</c>), else
/// matches the target's along-forward speed with a graduated throttle
/// (<c>Igc/shipIGC.cpp:2492-2512</c>). This task ports that single reduced band
/// and wires it to replace P162's fixed standoff-hold THROTTLE whenever the
/// Wingman is ENGAGED (within its engagement standoff).
///
/// <list type="bullet">
/// <item><b>Deterministic discriminators (loopback-free)</b> — directly invoke
/// the port's private <see cref="Ship.ComputeWingmanRangeThrottle"/> (via
/// reflection, the same pattern <c>NpcCrossClusterLiveTests</c>/
/// <c>NpcWingmanLeadLiveTests</c> already use for private members) on
/// geometrically-constructed ships and assert each of band 1's three outcomes
/// exactly: closing too fast → <c>−1</c> (back off), falling behind → <c>+1</c>
/// (press in), and the graduated match-speed formula → its exact value. These
/// fail cleanly if the logic is removed or wrong, with no sim variance.</item>
/// <item><b>Live loopback discriminator</b> — a real Wingman NPC in a booted
/// server engages an enemy pinned at a FIXED separation inside the standoff,
/// whose velocity is flipped between inbound (closing fast) and outbound
/// (fleeing). Because the separation never changes, P162's distance-only ramp
/// would give the SAME throttle in both cases — so the two measurements
/// differing in the expected directions (back off vs press in) proves the
/// throttle is responding to the target's closing SPEED, the new behaviour
/// that fails if the wiring were removed.</item>
/// </list>
/// Harness mirrors P162's <c>NpcWingmanAttackLiveTests</c>.
/// </summary>
[Collection("LiveNetwork")]
public sealed class NpcWingmanRangeMaintenanceLiveTests
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
            new Gm { Name = "P171 Wingman Range Maintenance Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 558u, DwCookie = 0x558u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 55400000000000005ul, DrmHash = "p171wingman",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static (Srv Server, Mission Mission, Sector Sector, short NpcSide, short PeerSide, short WingmanDroneId)
        Boot()
    {
        var server = new Srv();
        Mission? mission = null;
        Sector? sector = null;
        short npcSide = 0, peerSide = 0, wingDroneId = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            var core = m.Core;
            peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;
            wingDroneId = core.DroneTypes.First(kv => kv.Value.PilotType == Ship.PilotTypeWingman).Key;
            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = new Ship(id, hull, new Vector3(990f, 634f, 622f), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        // P178: keep the bot SEATED (no `using` → not disposed at Boot return)
        // so the mission stays Started — the server's win-by-elimination now
        // (correctly) ends a mission when its last player leaves. The server
        // closes this socket at teardown.
        var bot = new FltBot();
        bot.Connect(server.BoundPort, "p171_wingman");
        bot.SendLogonRequest(Request("p171_wingman"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);

        Assert.NotNull(mission); Assert.NotNull(sector);
        Assert.True(wingDroneId != 0, "core must expose a Wingman pilot-type drone");
        return (server, mission!, sector!, npcSide, peerSide, wingDroneId);
    }

    /// <summary>The first fightable hull+weapon (dispersion-0, power&gt;0) —
    /// same loadout the P162 live test selects, so the mounted weapon gives a
    /// real non-zero engagement range.</summary>
    private static (DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC proj) FirstFightable(StaticCore core)
    {
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
                    return (hull, p, pt);
            }
        }
        throw new InvalidOperationException("no fightable hull+weapon in the representative core");
    }

    /// <summary>Reflection access to the port's private instance
    /// <c>Ship.ComputeWingmanRangeThrottle</c> (same pattern as
    /// <c>NpcWingmanLeadLiveTests</c>'s <c>SolveLead</c>).</summary>
    private static float RangeThrottle(Ship wm, Ship target)
    {
        var mi = typeof(Ship).GetMethod("ComputeWingmanRangeThrottle", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Ship.ComputeWingmanRangeThrottle not found");
        return (float)(mi.Invoke(wm, new object[] { target }) ?? 0f);
    }

    /// <summary>Build a Wingman-shaped ship aimed along +X with the given own
    /// velocity, and a target on +X with the given velocity.</summary>
    private static (Ship wm, Ship target) Setup(StaticCore core, Vector3 ownVel, Vector3 targetVel)
    {
        var hull = core.HullTypes.Values.First(h => h.Speed > 0f);
        var wm = new Ship((short)1, hull, Vector3.Zero, ownVel, Orientation.Identity);
        wm.Orientation.SetForward(new Vector3(1f, 0f, 0f));           // nose on +X
        var target = new Ship((short)2, hull, new Vector3(100f, 0f, 0f), targetVel, Orientation.Identity);
        return (wm, target);
    }

    /// <summary>⭐ Deterministic discriminator 1: a target moving TOWARD the
    /// Wingman (closing too fast, relativeSpeed &gt; 20) must produce a back-off
    /// throttle of −1. Fails if the band-1 back-off branch is removed or wrong.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Closing_too_fast_backs_off()
    {
        Require.Artwork();
        var core = Core();
        // Wingman flying slowly +X; target 100 ahead rushing back toward it −X at
        // 60 u/s → relativeSpeed = own(+5) − targetDir(−60) = +65 &gt; 20 → back off.
        var (wm, target) = Setup(core, ownVel: new Vector3(5f, 0f, 0f), targetVel: new Vector3(-60f, 0f, 0f));
        float throttle = RangeThrottle(wm, target);
        Console.WriteLine($"P171-CLOSE: relativeSpeed=65, throttle={throttle}");
        Assert.True(throttle <= -0.999f,
            $"closing too fast must back off (Throttle −1): got {throttle}");
    }

    /// <summary>⭐ Deterministic discriminator 2: a target fleeing AWAY along +X
    /// (falling behind, relativeSpeed &lt; −20) must produce a press-in throttle
    /// of +1. Fails if the press-in branch is removed or wrong.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Falling_behind_presses_in()
    {
        Require.Artwork();
        var core = Core();
        // Wingman flying slowly +X; target fleeing +X at 60 → relativeSpeed =
        // own(+5) − targetDir(+60) = −55 &lt; −20 → press in.
        var (wm, target) = Setup(core, ownVel: new Vector3(5f, 0f, 0f), targetVel: new Vector3(60f, 0f, 0f));
        float throttle = RangeThrottle(wm, target);
        Console.WriteLine($"P171-FLEE: relativeSpeed=-55, throttle={throttle}");
        Assert.True(throttle >= 0.999f,
            $"falling behind must press in (Throttle +1): got {throttle}");
    }

    /// <summary>⭐ Deterministic discriminator 3: the MATCH-SPEED branch — when
    /// neither threshold trips, the throttle must equal native's graduated
    /// formula 2·targetDirSpeed/maxSpeed − 1 exactly (shipIGC.cpp:2507-2509).
    /// Wingman and target both move along +X at 40% of max speed → relativeSpeed
    /// 0 (match), targetDirSpeed = 0.4·maxSpeed → throttle = −0.2.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Match_speed_uses_native_graduated_formula()
    {
        Require.Artwork();
        var core = Core();
        var hull = core.HullTypes.Values.First(h => h.Speed > 0f);
        float maxSpeed = hull.Speed;
        float frac = 0.4f * maxSpeed;
        var (wm, target) = Setup(core, ownVel: new Vector3(frac, 0f, 0f), targetVel: new Vector3(frac, 0f, 0f));
        float throttle = RangeThrottle(wm, target);
        float expected = 2f * frac / maxSpeed - 1f;                     // = −0.2
        Console.WriteLine($"P171-MATCH: maxSpeed={maxSpeed}, targetDir={frac}, throttle={throttle} (expected {expected})");
        Assert.True(MathF.Abs(throttle - expected) < 1e-4f,
            $"match-speed must equal the graduated formula {expected}: got {throttle}");
    }

    /// <summary>⭐ Deterministic discriminator 4: the match-speed formula's two
    /// bounds — a target at/above max speed → throttle +1; a stationary target
    /// → throttle −1 (targetDirSpeed ≤ 0).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Match_speed_bounds_match_native()
    {
        Require.Artwork();
        var core = Core();
        var hull = core.HullTypes.Values.First(h => h.Speed > 0f);
        float maxSpeed = hull.Speed;

        // Target moving at max speed along +X, Wingman matching it → throttle 1.
        var (wm1, t1) = Setup(core, ownVel: new Vector3(maxSpeed, 0f, 0f), targetVel: new Vector3(maxSpeed, 0f, 0f));
        Assert.True(RangeThrottle(wm1, t1) >= 0.999f, "target at max speed → throttle +1");

        // Stationary target, Wingman stationary → targetDirSpeed 0 ≤ 0 → throttle −1.
        var (wm2, t2) = Setup(core, ownVel: Vector3.Zero, targetVel: Vector3.Zero);
        Assert.True(RangeThrottle(wm2, t2) <= -0.999f, "stationary target → throttle −1 (targetDirSpeed ≤ 0)");
    }

    /// <summary>⭐ Live loopback discriminator: a real Wingman NPC engages an
    /// enemy pinned at a FIXED separation inside the engagement standoff, and
    /// the target's velocity is flipped between inbound (closing fast) and
    /// outbound (fleeing). With the separation constant, the OLD distance-only
    /// ramp would issue the SAME throttle for both — so the two measurements
    /// differing in the expected directions (back off vs press in) proves the
    /// throttle responds to the target's closing SPEED. Removing the P171
    /// wiring makes both measurements equal the fixed-standoff value and this
    /// test fails.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Wingman_throttle_responds_to_target_closing_speed()
    {
        Require.Artwork();
        var (srv, mission, sector, npcSide, peerSide, wingDroneId) = Boot();
        using (srv)
        {
            var core = mission.Core;
            var (fhull, weapon, proj) = FirstFightable(core);

            Vector3 wp = new(500f, 540f, 510f);
            var wingman = srv.SpawnNpcShip(mission, sector, npcSide, wp, wingDroneId);
            Assert.Equal(Ship.PilotTypeWingman, wingman.PilotType);
            wingman.ChangeHull(fhull);
            wingman.Energy = fhull.MaxEnergy * 100f;
            wingman.Ammo = 10000;
            wingman.MountWeapon(weapon);

            // The engagement standoff the production code sizes for this weapon
            // (ApplyNpcAutopilot combat branch: min(0.7·range, 350)).
            float fireRange = proj.Speed * proj.Lifespan;
            float standoff = Math.Min(fireRange * 0.7f, 350f);
            // Pin the enemy at a FIXED separation clearly INSIDE the standoff so
            // the Wingman is ENGAGED (band 1 active) — the whole discriminator.
            float sep = Math.Max(standoff * 0.667f, 1f);
            Console.WriteLine($"P171-LIVE: fireRange={fireRange:F1}, standoff={standoff:F1}, sep={sep:F1}");

            var enemy = new Ship((short)5001, fhull, wingman.Position + new Vector3(sep, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            mission.PlaceShip(enemy, sector);
            wingman.AttackTarget = enemy;                                  // directly committed, bypassing nearest-search noise

            const float approach = 120f;
            // Native range control uses manual thrust buttons with base throttle
            // (Igc/shipIGC.cpp:2494-2509), replacing P171's throttle substitute.
            (int Buttons, float ForwardSpeed) MovementFor(Vector3 targetVel)
            {
                wingman.Position = wp;
                wingman.Velocity = Vector3.Zero;
                wingman.StateM = 0;
                wingman.Orientation.Set(new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f));
                for (int axis = 0; axis < 3; axis++) wingman.SetTurnRate(axis, 0);
                enemy.Position = wp + new Vector3(sep, 0f, 0f);
                enemy.Velocity = targetVel;
                srv.TickSim();
                return (wingman.StateM & ShipControlState.MoveButtonsMask, wingman.Velocity.X);
            }
            var close = MovementFor(new Vector3(-approach, 0f, 0f));
            var flee = MovementFor(new Vector3(approach, 0f, 0f));
            Console.WriteLine($"P708 native range: closing={close}, fleeing={flee}");
            Assert.Equal(ShipControlState.BackwardButton, close.Buttons);
            Assert.Equal(ShipControlState.ForwardButton, flee.Buttons);
            Assert.True(close.ForwardSpeed < 0, $"Backward input must produce real braking: {close}");
            Assert.True(flee.ForwardSpeed > 0, $"Forward input must produce real acceleration: {flee}");
            Assert.Same(enemy, wingman.AttackTarget);
        }
    }
}
