using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P370 — LIVE proof of the "ship left your view" fix at the real wire level:
/// a real in-process server, real TCP transport, and real
/// <see cref="ConnectionSession"/> peers driving real <see cref="ClientWorld"/>s.
/// The reported ghost-ship scenario was: one ship warps/docks to leave a
/// sector while a teammate stays behind; the teammate's client kept rendering
/// the departed ship frozen because nothing told it "this ship is gone."
/// These tests reproduce that with the server's new <c>SHIP_DELETE(20)</c>
/// (<c>SDR_LEFTSECTOR</c>/<c>SDR_DOCKED</c>) broadcast and assert the staying
/// peer's <see cref="ClientWorld"/> actually latches
/// <see cref="ShipEntity.IsGone"/> (so the render/target loops hide it), while
/// the departing ship's own peer is NOT told to hide its own vessel. The dock
/// case also drives the reverse transition (undock via <c>FM_C_DOCKED</c>): the
/// returned ship clears <c>IsGone</c> and resumes visibility.
/// </summary>
[Collection("LiveNetwork")]
public sealed class ShipDeleteGoneLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    private static Cfg Config(string name) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = name, Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 977u, DwCookie = 0x977u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 97600000000000001ul, DrmHash = "p370",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Ship? ServerShip(Srv server, short id)
    {
        // `server.Ships` is internal (friend-gated to Core.Tests only) — reach
        // the live ship through the public mission model instead.
        foreach (var m in server.Missions)
            foreach (var sector in m.Sectors)
                foreach (var model in sector.Models)
                    if (model is Ship s && s.Id == id)
                        return s;
        return null;
    }

    private static bool Ent(SessionView s, short id, out ShipEntity e) =>
        s.Ents.TryGetValue(id, out e!);

    /// <summary>A thread-safe snapshot of one client's ship entities (read under
    /// the session gate) so assertions never race the receive thread.</summary>
    private sealed record SessionView(ConnectionSession S, Dictionary<short, ShipEntity> Ents, short Me)
    {
        public static SessionView Of(ConnectionSession s)
        {
            lock (s.Gate)
            {
                var ents = s.World.Ships.ToDictionary(kv => kv.Key, kv => kv.Value);
                return new SessionView(s, ents, s.World.MyShipId ?? -1);
            }
        }
    }

    private static ConnectionSession Connect(Srv server, string name)
    {
        var s = new ConnectionSession();
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(Request(name));
        // All three scenes assign watcher/departer roles by arrival order.
        // Complete this seat before the caller starts the next connection.
        Assert.True(PumpUntil(server, () => s.World.MyShipId is not null, 4000) < 4000,
            $"{name} did not seat");
        return s;
    }

    /// <summary>⭐ Same-side warp-out: the watcher (still in sector A) must latch
    /// the warper as IsGone the moment it transits; the warper's own peer must
    /// NOT hide its own ship. This is the exact reported live-playtest bug.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SameSide_WarperLeavesSector_WatcherLatchesGone_OwnPeerNotHidden()
    {
        Require.Artwork();
        using var server = new Srv();
        int placed = 0;
        Ship? warper = null; Sector? sec0 = null; ResolvedWarp? awayWarp = null;
        Vector3 warpCenter = default; Vector3 outDir = default; float hullLen = 1f; float warpRadius = 100f;

        SetShipSpawner(server, (core, mission, id) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            hullLen = hull.Length;
            var s0 = mission.Sectors[0];
            sec0 = s0;
            short side = mission.Sides[0].Id;
            if (placed == 0)   // the watcher — parked, far from any warp
            {
                placed++;
                var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = side };
                mission.PlaceShip(ship, s0);
                return (ship, s0);
            }
            // the warper — parked OUTSIDE a warp in sector A (stationary at
            // spawn so the watcher learns it first; we fling it in afterward).
            var w = mission.Warps.First(w2 => w2.Destination is not null && w2.Partner() is not null && s0.Warps.Contains(w2));
            awayWarp = w;
            warpCenter = new Vector3(w.Warp.Position![0], w.Warp.Position[1], w.Warp.Position[2]);
            warpRadius = w.Warp.Radius;
            float[] f = w.Warp.Forward is { Length: 3 } v && (v[0]*v[0]+v[1]*v[1]+v[2]*v[2]) > 0f ? v : [1f, 0f, 0f];
            outDir = new Vector3(f[0], f[1], f[2]).Normalize();
            var spawn = warpCenter + outDir * (3.0f * (w.Warp.Radius + 0.5f * hull.Length));
            var ws = new Ship(id, hull, spawn, Vector3.Zero, Orientation.Identity) { SideId = side };
            mission.PlaceShip(ws, s0);
            warper = ws;
            placed++;
            return (ws, s0);
        });

        server.Boot(Config("P370 Same-Side Warp"));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var watcher = Connect(server, "WatcherAlpha");
        using var dep = Connect(server, "DeparterAlpha");
        int seated = PumpUntil(server,
            () => watcher.World.MyShipId is { } && dep.World.MyShipId is { }, 4000);
        Assert.True(seated < 4000, "both peers seated");
        short depMe = dep.World.MyShipId!.Value;

        // Baseline: the watcher has LEARNED the departer (same-side teammate,
        // same sector) and it is NOT gone.
        int learned = PumpUntil(server,
            () => Ent(SessionView.Of(watcher), depMe, out var e) && !e.IsGone, 3000);
        Assert.True(learned < 3000, "watcher learned the departer before transit");
        Assert.False(SessionView.Of(watcher).Ents[depMe].IsNotVisible);

        // Drop the warper just INSIDE the warp's collision volume, moving toward
        // its centre — ResolveWarpCollisions fires on ANY overlap, so it transits
        // on the next tick (the same arrangement WarpTransitServerTests uses;
        // a plain velocity fling from outside stalls on drag). It was already
        // visible to the watcher while parked outside, so the deletion is real.
        lock (server)
        {
            float r = warpRadius + 0.5f * hullLen;
            warper!.Position = warpCenter + outDir * (0.9f * r);
            warper.Velocity = -outDir * 20f;
        }
        int transited = PumpUntil(server,
            () => ServerShip(server, depMe)?.Sector?.Id != sec0!.Id, 4000);
        Assert.True(transited < 4000, "departer transited to another sector");

        // The watcher must mark the departer gone (promptly — not frozen).
        int gone = PumpUntil(server,
            () => Ent(SessionView.Of(watcher), depMe, out var e) && e.IsGone, 2000);
        Assert.True(gone < 2000, "watcher marked the departed same-side ship IsGone");
        Assert.True(SessionView.Of(watcher).Ents[depMe].IsNotVisible,
            "departed ship is excluded from render/target on the watcher");

        // The departer's OWN peer is not told to hide its own vessel.
        Assert.False(SessionView.Of(dep).Ents[depMe].IsGone,
            "the departing peer's own ship is not hidden by its own departure");

        // ---- REVERSE: warp BACK into the watcher's sector. The departer is
        // now in the destination sector; slide it into the RETURN warp (the
        // partner of the A-side warp) so it transits back, then verify the
        // watcher clears IsGone and renders it again (not permanently hidden).
        var returnWarp = awayWarp!.Partner()!;
        var rc = new Vector3(returnWarp.Warp.Position![0], returnWarp.Warp.Position[1], returnWarp.Warp.Position[2]);
        float[] rfw = returnWarp.Warp.Forward is { Length: 3 } rv && (rv[0]*rv[0]+rv[1]*rv[1]+rv[2]*rv[2]) > 0f ? rv : [1f, 0f, 0f];
        var rdir = new Vector3(rfw[0], rfw[1], rfw[2]).Normalize();
        lock (server)
        {
            warper!.Position = rc + rdir * (0.9f * (returnWarp.Warp.Radius + 0.5f * hullLen));
            warper.Velocity = -rdir * 20f;
        }
        int backSector = PumpUntil(server,
            () => ServerShip(server, depMe)?.Sector?.Id == sec0!.Id, 4000);
        Assert.True(backSector < 4000, "departer warped back into the watcher's sector");
        // Park it away from the A-side warp so the 2s debounce lapse can't cause
        // an immediate re-transit that would re-mark it gone.
        lock (server)
        {
            warper.Position = warpCenter + outDir * (4.0f * (warpRadius + 0.5f * hullLen));
            warper.Velocity = Vector3.Zero;
        }
        int returned = PumpUntil(server,
            () => Ent(SessionView.Of(watcher), depMe, out var e) && !e.IsGone, 3000);
        Assert.True(returned < 3000, "watcher cleared IsGone when the departer warped back into view");
        Assert.False(SessionView.Of(watcher).Ents[depMe].IsNotVisible,
            "returned ship is visible again (not permanently hidden)");
    }

    /// <summary>⭐ Cross-side warp-out: a BRAVO ship warps out of the sector an
    /// ALPHA watcher is in; the cross-side watcher's ClientWorld still latches
    /// IsGone (it had the ship via P369 identity + telemetry).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void CrossSide_WarperLeavesSector_WatcherLatchesGone()
    {
        Require.Artwork();
        using var server = new Srv();
        int placed = 0;
        Ship? warper = null; Sector? sec0 = null;
        Vector3 warpCenter = default; Vector3 outDir = default; float warpRadius = 100f; float hullLen = 1f;

        SetShipSpawner(server, (core, mission, id) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            hullLen = hull.Length;
            var s0 = mission.Sectors[0];
            sec0 = s0;
            if (placed == 0)   // watcher on side 0 (Alpha)
            {
                placed++;
                var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = mission.Sides[0].Id };
                mission.PlaceShip(ship, s0);
                return (ship, s0);
            }
            // departer on side 1 (Bravo), parked outside a warp
            var w = mission.Warps.First(w2 => w2.Destination is not null && w2.Partner() is not null && s0.Warps.Contains(w2));
            warpCenter = new Vector3(w.Warp.Position![0], w.Warp.Position[1], w.Warp.Position[2]);
            warpRadius = w.Warp.Radius;
            float[] f = w.Warp.Forward is { Length: 3 } v && (v[0]*v[0]+v[1]*v[1]+v[2]*v[2]) > 0f ? v : [1f, 0f, 0f];
            outDir = new Vector3(f[0], f[1], f[2]).Normalize();
            var spawn = warpCenter + outDir * (3.0f * (w.Warp.Radius + 0.5f * hull.Length));
            var ws = new Ship(id, hull, spawn, Vector3.Zero, Orientation.Identity) { SideId = mission.Sides[1].Id };
            mission.PlaceShip(ws, s0);
            warper = ws;
            placed++;
            return (ws, s0);
        });

        server.Boot(Config("P370 Cross-Side Warp"));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var alpha = Connect(server, "AlphaWatcher");
        using var bravo = Connect(server, "BravoDeparter");
        int seated = PumpUntil(server,
            () => alpha.World.MyShipId is { } && bravo.World.MyShipId is { }, 4000);
        Assert.True(seated < 4000, "both peers seated");
        short bravoId = bravo.World.MyShipId!.Value;

        // Cross-side identity + telemetry reach the Alpha watcher (P369 gate:
        // a seated Alpha peer is in the departer's sector). Learn it, not gone.
        int learned = PumpUntil(server,
            () => Ent(SessionView.Of(alpha), bravoId, out var e) && !e.IsGone, 3000);
        Assert.True(learned < 3000, "cross-side watcher learned the departer before transit");

        // Drop the bravo ship just INSIDE the warp's collision volume (any
        // overlap transits it on the next tick), so the alpha watcher sees it
        // disappear. Already visible via P369 identity + telemetry before.
        lock (server)
        {
            float r = warpRadius + 0.5f * hullLen;
            warper!.Position = warpCenter + outDir * (0.9f * r);
            warper.Velocity = -outDir * 20f;
        }
        int transited = PumpUntil(server,
            () => ServerShip(server, bravoId)?.Sector?.Id != sec0!.Id, 4000);
        Assert.True(transited < 4000, "bravo ship transited");

        int gone = PumpUntil(server,
            () => Ent(SessionView.Of(alpha), bravoId, out var e) && e.IsGone, 2000);
        Assert.True(gone < 2000, "cross-side watcher marked the departed ship IsGone");
        Assert.True(SessionView.Of(alpha).Ents[bravoId].IsNotVisible);
    }

    /// <summary>⭐ Dock-out + the LIVE reverse (undock): a docked ship leaves the
    /// watcher's view (SDR_DOCKED -> IsGone); on undock (FM_C_DOCKED) telemetry
    /// resumes and the watcher's ClientWorld clears IsGone — the ship is back in
    /// view, exactly the "comes back into view" requirement.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void DockOut_WatcherLatchesGone_UndockReturnsAndClears()
    {
        Require.Artwork();
        using var server = new Srv();
        int placed = 0;
        Ship? docker = null; Sector? dockSector = null; Vector3 stationPos = default;

        SetShipSpawner(server, (core, mission, id) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            short side = mission.Sides[0].Id;
            // Resolve the dock target (an Alpha station) and its OWN sector once;
            // BOTH ships must live in that same sector so the watcher can see the
            // docker, and a station's sector need not be Sectors[0].
            if (dockSector is null)
            {
                var st = mission.Sectors.SelectMany(s2 => s2.Stations).First(s2 => s2.SideId == side);
                stationPos = new Vector3(st.Position[0], st.Position[1], st.Position[2]);
                dockSector = mission.Sectors.First(s2 => s2.Stations.Contains(st));
            }
            if (placed == 0)   // watcher — in the same sector but FAR from the dock (stays flying)
            {
                placed++;
                var ship = new Ship(id, hull, stationPos + new Vector3(0f, 0f, 1500f), Vector3.Zero, Orientation.Identity) { SideId = side };
                mission.PlaceShip(ship, dockSector);
                return (ship, dockSector);
            }
            // docker — parked JUST OUTSIDE the friendly station's DockRadius so
            // it stays flying (and observable as such) once the mission Starts;
            // the test slides it INTO the radius afterward to trigger the dock.
            var dship = new Ship(id, hull, stationPos + new Vector3(0f, 0f, 1000f), Vector3.Zero, Orientation.Identity) { SideId = side };
            mission.PlaceShip(dship, dockSector);
            docker = dship;
            placed++;
            return (dship, dockSector);
        });

        server.Boot(Config("P370 Dock"));

        using var watcher = Connect(server, "DockWatcher");
        using var dep = Connect(server, "Docker");
        int seated = PumpUntil(server,
            () => watcher.World.MyShipId is { } && dep.World.MyShipId is { }, 4000);
        Assert.True(seated < 4000, "both peers seated");
        short watcherMe = watcher.World.MyShipId!.Value;
        short depMe = dep.World.MyShipId!.Value;
        // P180/P188 gate: the per-tick dock-radius pass only runs for a
        // Started mission, reached by the real ready-up countdown. Both peers
        // are on the same (Alpha) side, so BOTH must ready for the side to be
        // fully ready — readying only the docker leaves Alpha not-ready and the
        // countdown (and thus the dock pass) never starts.
        watcher.SendPlayerReady(true, watcherMe);
        dep.SendPlayerReady(true, depMe);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 4000);
        Assert.True(started < 4000, "mission reached Started after both peers readied");

        // Baseline: watcher learned the docker (flying, outside dock radius),
        // not gone.
        int learned = PumpUntil(server,
            () => Ent(SessionView.Of(watcher), depMe, out var e) && !e.IsGone, 3000);
        Assert.True(learned < 3000, "watcher learned the docker before it docked");

        // Slide the docker into the station's DockRadius (it was spawned outside
        // so the watcher observed it flying first) — this triggers the dock pass.
        lock (server)
        {
            var arriving = docker!;
            var home = dockSector!.Stations.First(st => st.SideId == arriving.SideId);
            arriving.Position = DockTestPlacement.Inside(arriving.Mission!, home, arriving.HullType);
        }

        // The docker docks (its OWN world latches StationId).
        int docked = PumpUntil(server,
            () => SessionView.Of(dep).Ents.TryGetValue(depMe, out var ee) && ee.StationId != -1, 8000);
        Assert.True(docked < 8000, "docker's own ship latched StationId (really docked)");

        // Watcher latches the docker as gone.
        int gone = PumpUntil(server,
            () => Ent(SessionView.Of(watcher), depMe, out var e) && e.IsGone, 2000);
        Assert.True(gone < 2000, "watcher marked the docked ship IsGone");
        Assert.True(SessionView.Of(watcher).Ents[depMe].IsNotVisible);
        // The docker's own peer is not hidden by its own docking.
        Assert.False(SessionView.Of(dep).Ents[depMe].IsGone);

        // ---- REVERSE: undock (FM_C_DOCKED, stationId = NA) — the ship returns
        // to the sector, telemetry resumes, and the watcher clears IsGone. ----
        dep.SendDocked();
        int freed = PumpUntil(server,
            () => SessionView.Of(dep).Ents.TryGetValue(depMe, out var ee) && ee.StationId == -1, 8000);
        Assert.True(freed < 8000, "docker undocked (StationId cleared)");

        int back = PumpUntil(server,
            () => Ent(SessionView.Of(watcher), depMe, out var e) && !e.IsGone, 3000);
        Assert.True(back < 3000, "watcher cleared IsGone when the ship returned to view");
        Assert.False(SessionView.Of(watcher).Ents[depMe].IsNotVisible,
            "returned ship is visible again (not permanently hidden)");
    }
}
