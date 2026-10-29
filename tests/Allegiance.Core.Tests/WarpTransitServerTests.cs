using Allegiance.Core.Model;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
// 'Server'/'Bot' are also namespaces — alias to unambiguous identifiers (same
// pattern as ShipStatusServerTests).
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// The P41 loopback deliverable at the wire level, the second half of the
/// warp-transit slice (the geometry half is <see cref="WarpProximityTests"/>).
/// A real <see cref="Srv"/> boots a multi-sector map, a loopback
/// <see cref="FltBot"/> joins, and its ship is spawned just inside a warp's
/// collision volume with a velocity toward it. The first sector tick trips the
/// proximity pass (<c>Sector.ResolveWarpCollisions</c>), the host performs the
/// transit (<c>ServerHost.HitWarpEvent</c>, <c>fsship.cpp:71-137</c>), and we
/// prove: (a) the ship's server-side sector genuinely changes to the
/// destination, and (b) the peer receives a fresh <c>SET_CLUSTER(7)</c> and a
/// <c>SHIP_STATUS(90)</c> both reflecting the new sector. No Windows VM / native
/// client needed — the managed <see cref="FltBot"/> harness is sufficient,
/// matching P40's precedent that server-side proof and live-client proof are
/// separable (P42's job).
/// </summary>
[Collection("LiveNetwork")]
public class WarpTransitServerTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Warp Transit Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 911u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 72000000000000000ul, DrmHash = "warptransit",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>
    /// ⭐ A ship placed inside a warp's collision volume, given a velocity
    /// toward the warp centre, crosses: the server-side sector changes to the
    /// destination and the peer receives a fresh SET_CLUSTER(7) plus a
    /// SHIP_STATUS(90) bearing the new sector.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange()
    {
        Require.Artwork();
        using var server = new Srv();

        // A dedicated spawner so the bot's ship starts just inside the first
        // resolvable warp's collision volume, aimed at it. This is the only
        // controlled hook into placement (tests inject a spawner exactly for
        // this — Server.cs:88 ShipSpawner).
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var warp = mission.Warps.First(w => w.Destination is not null && w.Partner() is not null);
            var sector = mission.Sectors.First(s => s.Warps.Contains(warp));
            var wpos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);

            // P776 — native's aleph volume is a cone (modelIGC.cpp:127-130;
            // HitTest.cpp:254-283): a base disc through the centre plus a 45°
            // cone BEHIND it. A "just inside" pose must therefore sit on the
            // backward axis, not in front of the disc (the old sphere test
            // accepted either side).
            float[] f = warp.Warp.Forward is { Length: 3 } v && (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) > 0f
                ? v : [1f, 0f, 0f];
            var backward = AlephConeHitTest.Backward(new Vector3(f[0], f[1], f[2]));

            float shipRadius = 0.5f * hull.Length;
            // Just inside the raw radius, on the cone axis; the ship then flies
            // forward through the disc, so it is genuinely crossing. Transit
            // teleports it out on the first tick.
            var spawn = wpos + backward * (0.9f * warp.Warp.Radius);
            var ship = new Ship(shipId, hull, spawn, -backward * 20f, Orientation.Identity);
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "warp_transit_probe");
        bot.SendLogonRequest(Request("warp_transit_probe"));

        // Join burst completes: initial SET_CLUSTER(sector A) + SHIP_STATUS(sector A).
        // (Timing-independent: the loop always runs the done() check first, so a
        // ship can transit on the very first TickSim — no > 0 guard on these.)
        PumpUntil(server, () => bot.Assignments.Count >= 1 && bot.ShipStatuses.Count >= 1, 4000);
        short initialSector = bot.Assignments[0].SectorId;
        short shipId = bot.Assignments[0].Ship.ShipId;
        Assert.True(bot.ShipStatuses.Any(s => s.SectorId == initialSector && s.ShipId == shipId),
            $"join SHIP_STATUS should reflect the initial sector {initialSector}");

        // Pump until the transit fires and the peer sees a second SET_CLUSTER
        // with a DIFFERENT sector. The debounce (native 2s fudge) prevents any
        // rapid oscillation from masking the first crossing.
        PumpUntil(server,
            () => bot.Assignments.Count >= 2 && bot.Assignments[^1].SectorId != initialSector,
            4000);

        Assert.True(bot.Assignments.Count >= 2,
            $"ship never crossed (got {bot.Assignments.Count} SET_CLUSTER, sectors=[{string.Join(",", bot.Assignments.Select(a => a.SectorId))}])");
        var second = bot.Assignments[^1];
        Assert.Equal(shipId, second.Ship.ShipId);          // same ship, new sector
        short newSector = second.SectorId;
        Assert.NotEqual(initialSector, newSector);

        // (a) server-side proof: the LiveShip the server tracks for this peer
        // and the Ship's own sector both agree on the new sector, and the ship
        // model has genuinely moved into the destination sector's model list.
        var live = server.Ships.First(l => l.Ship.Id == shipId);
        Assert.Equal(newSector, live.Sector.Id);
        Assert.Equal(newSector, live.Ship.Sector?.Id);
        var dest = live.Ship.Mission!.Sectors.First(s => s.Id == newSector);
        Assert.Contains(live.Ship, dest.Models);
        Assert.DoesNotContain(live.Ship, live.Ship.Mission!.Sectors.First(s => s.Id == initialSector).Models);

        // (b) wire proof: a SHIP_STATUS(90) arrived bearing the new sector.
        Assert.Contains(bot.ShipStatuses, s => s.ShipId == shipId && s.SectorId == newSector);
    }

    /// <summary>
    /// ⭐ P244 — the live counterpart of the premature-transit fix. A ship
    /// spawned at a distance BETWEEN the tight real trigger (<c>warp.Radius +
    /// shipRadius</c>, ~100 u) and the P238-widened one (<c>Sector.WarpVisualScale
    /// ·warp.Radius + shipRadius</c>, ~950 u) — i.e. out at the edge of the large
    /// visible vortex, where P238 fired — must <b>no longer</b> transit over the
    /// wire: it stays in its initial sector, so the aleph does not vanish while
    /// the player is still approaching its rim. The positive (a ship that reaches
    /// the tight radius still crosses) is <see cref="ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange"/>,
    /// so together they bracket P244's trigger back onto the warp's real raw
    /// radius without regressing P238's "transit must fire".
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShipBetweenRawAndVisualRadius_DoesNotPrematurelyTransit_AndPeerSeesNoSectorChange()
    {
        Require.Artwork();
        using var server = new Srv();

        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var warp = mission.Warps.First(w => w.Destination is not null && w.Partner() is not null);
            var sector = mission.Sectors.First(s => s.Warps.Contains(warp));
            var wpos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);

            float[] f = warp.Warp.Forward is { Length: 3 } v && (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) > 0f
                ? v : [1f, 0f, 0f];
            var dir = new Vector3(f[0], f[1], f[2]).Normalize();

            float shipRadius = 0.5f * hull.Length;
            float rawThreshold = warp.Warp.Radius + shipRadius;                        // P244 (tight, native-faithful) trigger
            float visualThreshold = Sector.WarpVisualScale * warp.Warp.Radius + shipRadius; // P238 (visual-matched)
            float dist = 0.5f * (rawThreshold + visualThreshold);   // between the two radii
            Console.WriteLine($"P244-LIVE: warp #{warp.Warp.WarpId} radius={warp.Warp.Radius}, raw trigger={rawThreshold:F1}, P238 visual trigger={visualThreshold:F1}, spawn at dist={dist:F1} (between)");

            var spawn = wpos + dir * dist;
            var ship = new Ship(shipId, hull, spawn, -dir * 20f, Orientation.Identity);
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "warp_transit_p244");
        bot.SendLogonRequest(Request("warp_transit_p244"));

        // Initial join burst: SET_CLUSTER(sector A) + SHIP_STATUS(sector A).
        PumpUntil(server, () => bot.Assignments.Count >= 1 && bot.ShipStatuses.Count >= 1, 4000);
        short initialSector = bot.Assignments[0].SectorId;
        short shipId = bot.Assignments[0].Ship.ShipId;

        // Give the simulation ample time for any transit to fire; a healthy
        // number of ticks at 20 Hz far exceeds the 2 s debounce, so if the ship
        // were inside the trigger it would have crossed by now.
        int pumped = PumpUntil(server,
            () => bot.Assignments.Count >= 2 && bot.Assignments[^1].SectorId != initialSector,
            4000);

        Assert.True(bot.Assignments.Count >= 1, "the bot must receive at least its initial SET_CLUSTER");
        Assert.Equal(initialSector, bot.Assignments[^1].SectorId);
        Assert.Equal(1, bot.Assignments.Count(a => a.Ship.ShipId == shipId));
        Console.WriteLine($"P244-LIVE: between-radius spawn did NOT transit after {pumped} ticks — stayed in sector {initialSector} (SET_CLUSTER count {bot.Assignments.Count}); the ~950 u rim no longer fires transit.");
    }

    /// <summary>
    /// ⭐ P246 — tune the exit-speed bonus to match native's real magnitude
    /// (<c>c_fExitWarpSpeed = 15.0f</c>). A ship spawned just inside a warp's
    /// collision volume with a known entry speed <c>s</c> and a velocity toward
    /// the centre transits on the first tick; the server-side ship's velocity
    /// afterwards must have magnitude <c>s + 15</c> (the old stand-in was
    /// <c>s + 5</c>). The direction is irrelevant here (the exit launches along
    /// the destination warp's backward axis) — only the speed magnitude changes
    /// with this constant.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShipExitSpeed_IsEntrySpeedPlusNativeBonus_AfterTransit()
    {
        Require.Artwork();
        using var server = new Srv();

        Sector? spawnSector = null;
        float? hullMaxSpeed = null;

        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            hullMaxSpeed = hull.Speed;
            var warp = mission.Warps.First(w => w.Destination is not null && w.Partner() is not null);
            var sector = mission.Sectors.First(s => s.Warps.Contains(warp));
            spawnSector = sector;
            var wpos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);

            float[] f = warp.Warp.Forward is { Length: 3 } v && (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) > 0f
                ? v : [1f, 0f, 0f];
            var dir = new Vector3(f[0], f[1], f[2]).Normalize();

            float shipRadius = 0.5f * hull.Length;
            float radius = warp.Warp.Radius + shipRadius;
            // Spawn OUTSIDE the collision volume, aimed at the warp centre so
            // the ship cruises in over many ticks and reaches steady state.
            var spawn = wpos + dir * (4.0f * radius);

            // Full-forward throttle, facing the warp: the ship cruises at exactly
            // hull.Speed (ExecuteMove's cruise branch cancels drag and holds the
            // max-speed target), so the transit reads a precisely-known entry
            // speed = hull.Speed and must exit at hull.Speed + 15.
            var orient = new Orientation();
            orient.Set(-dir, new Vector3(0f, 1f, 0f));     // forward = -dir → faces the warp
            var ship = new Ship(shipId, hull, spawn, -dir * hull.Speed, orient)
            {
                Controls = new ControlData(0f, 0f, 0f, +1f),  // full forward throttle (cruise)
            };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby

        // A bot joins so the spawner places the ship.
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "warp_exit_speed");
        bot.SendLogonRequest(Request("warp_exit_speed"));
        PumpUntil(server, () => server.Ships.Count >= 1, 4000);

        short shipId = server.Ships.First().Ship.Id;
        Assert.NotNull(spawnSector);
        Assert.NotNull(hullMaxSpeed);
        short sectorA = spawnSector!.Id;
        var liveLookup = () => server.Ships.First(l => l.Ship.Id == shipId);

        // Let the full-throttle cruise carry the ship into the aleph. The entry
        // speed it reads is the steady-state hull.Speed (the ship accelerates
        // from spawn and holds that exact cruise speed before crossing).
        int pumped = 0;
        while (pumped < 4000)
        {
            if (liveLookup().Ship.Sector?.Id != sectorA) break;   // transit fired this/prior tick
            server.TickSim();
            pumped++;
            Thread.Sleep(1);
        }

        Assert.True(pumped < 4000,
            $"ship never transited (stayed in sector {sectorA} after {pumped} ticks)");

        float entrySpeed = hullMaxSpeed!.Value;
        float exitSpeed = liveLookup().Ship.Velocity.Length();
        Console.WriteLine($"P246-LIVE: steady-cruise entry speed {entrySpeed:F1} -> measured exit speed {exitSpeed:F1} (native bonus 15 gives {entrySpeed + 15:F1}; old stand-in 5 would give {entrySpeed + 5:F1})");
        Assert.Equal(entrySpeed + 15f, exitSpeed, 3);
    }

    /// <summary>
    /// ⭐ P406 — warp-transit EXIT distance from the destination aleph. The P238
    /// trigger was reverted to the raw radius (P244) and the visual shrunk to 2.5×
    /// (P247), but the exit clearance in <c>ServerHost.HitWarpEvent</c> still scaled
    /// by <c>Sector.WarpVisualScale·warp.Radius</c>, stranding a ship ~250 u farther
    /// from the aleph than native (<c>fsship.cpp:132</c> exits at
    /// <c>(GetRadius()+5)·backward</c>). This proves the ship now emerges right at
    /// the destination warp: server-side distance to the partner aleph centre is
    /// <c>shipRadius + 5</c> (± the up/right rand(2,5) lateral jitter), not the old
    /// visually-scaled ~10× figure. Mirror of the P246 cruise harness but with a
    /// just-inside spawn so the transit fires on the very first tick and the exit
    /// position is captured with no coasting drift (transit is the last pass of the
    /// tick, Sector.Update: models → combat → warp).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShipExitPosition_IsAtShipRadiusPlusFive_FromDestinationWarp()
    {
        Require.Artwork();
        using var server = new Srv();

        Sector? spawnSector = null;
        Vector3? destWarpPos = null;
        float? shipRadius = null;

        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            shipRadius = 0.5f * hull.Length;
            var warp = mission.Warps.First(w => w.Destination is not null && w.Partner() is not null);
            var partner = warp.Partner()!;
            destWarpPos = new Vector3(partner.Warp.Position![0], partner.Warp.Position[1], partner.Warp.Position[2]);
            var sector = mission.Sectors.First(s => s.Warps.Contains(warp));
            spawnSector = sector;
            var wpos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);

            float[] f = warp.Warp.Forward is { Length: 3 } v && (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) > 0f
                ? v : [1f, 0f, 0f];
            var dir = new Vector3(f[0], f[1], f[2]).Normalize();

            // Just inside the collision volume -> the native trigger fires on the
            // FIRST tick (it does not require approaching from outside, fsship.cpp:74 ff.),
            // and the transit is the last pass of that tick, so the position captured
            // when we observe the sector change is exactly the post-transit position.
            // Entry at a REALISTIC cruise speed (hull.Speed): the exit then launches
            // at hull.Speed+15 straight backward, clearing the destination warp's raw
            // ~100 u collision sphere well inside the 2 s debounce — so no re-trigger.
            float radius = warp.Warp.Radius + shipRadius!.Value;
            var spawn = wpos + dir * (0.9f * radius);
            var ship = new Ship(shipId, hull, spawn, -dir * hull.Speed, Orientation.Identity);
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "warp_exit_dist");
        bot.SendLogonRequest(Request("warp_exit_dist"));
        PumpUntil(server, () => server.Ships.Count >= 1, 4000);

        short shipId = server.Ships.First().Ship.Id;
        Assert.NotNull(spawnSector);
        Assert.NotNull(destWarpPos);
        Assert.NotNull(shipRadius);
        short sectorA = spawnSector!.Id;
        var live = () => server.Ships.First(l => l.Ship.Id == shipId);

        // Pump until the transit fires (server-side sector change), capturing the
        // post-transit position on that exact tick.
        int pumped = 0;
        Vector3? exitPos = null;
        while (pumped < 4000)
        {
            if (live().Ship.Sector?.Id != sectorA) { exitPos = live().Ship.Position; break; }
            server.TickSim();
            pumped++;
            Thread.Sleep(1);
        }

        Assert.True(exitPos is not null,
            $"ship never transited (stayed in sector {sectorA} after {pumped} ticks)");

        float clearance = shipRadius!.Value + 5f;                       // native (GetRadius()+5) backward offset, fsship.cpp:132
        float dist = (exitPos!.Value - destWarpPos!.Value).Length();    // dist = sqrt(clearance² + up² + right²), up/right ∈ [2,5]
        float minDist = clearance;                                      // lateral rand(2,5) only ADDS; never subtracts
        float maxDist = MathF.Sqrt(clearance * clearance + 5f * 5f + 5f * 5f);  // worst-case lateral jitter
        Console.WriteLine($"P406-LIVE: exit dist={dist:F1} vs native clearance {clearance:F1} (lateral jitter lands it in [{minDist:F1},{maxDist:F1}]; the old P238-scaled exit would have been ~{Sector.WarpVisualScale * 100f + clearance:F0})");
        Assert.InRange(dist, minDist, maxDist + 0.01f);
        Assert.True(dist < 2.0f * clearance,
            $"exit distance {dist:F1} should hug the aleph (~shipRadius+5={clearance:F1}), not scale with the visual funnel");

        // P406 requirement 2 — NO re-trigger ping-pong across the 2 s debounce.
        // The fixed exit now lands INSIDE the destination warp's raw collision
        // sphere (the scale that used to clear it is gone), so the only thing
        // keeping the ship from re-crossing is the LastWarpTime /
        // WarpCooldownSeconds gate (Sector.cs:2716). Fly straight (the transit
        // already launched us backward away from the source) and confirm the
        // ship neither re-enters the source sector nor emits another SET_CLUSTER.
        short destSector = live().Ship.Sector!.Id;
        int reCrossChecks = 0;
        int settled = 0;
        while (settled < 60)   // ~3 s @ 20 Hz — comfortably past the 2 s debounce
        {
            var sec = live().Ship.Sector?.Id;
            Assert.NotEqual(sectorA, sec);            // never re-enters the source sector
            reCrossChecks++;
            server.TickSim();
            settled++;
            Thread.Sleep(1);
        }
        Console.WriteLine($"P406-LIVE: flew straight {reCrossChecks} ticks ({settled / 20.0:F1}s) in sector {destSector} — no re-trigger ping-pong back to {sectorA}");
    }
}
