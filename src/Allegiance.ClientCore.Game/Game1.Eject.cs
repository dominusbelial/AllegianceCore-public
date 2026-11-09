using System;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

// P540 — the client-side EJECT consumer. Split into its own partial-class file
// so the wire application (ClientWorld.Apply(Eject)) stays separate from the
// MonoGame-side reaction: choosing the nearest friendly rescue target and
// engaging the existing P524 player autopilot toward it (native appmsg.cpp:230-237).
public sealed partial class Game1
{
    /// <summary>The eject-target capability mask for a STATION, mirroring
    /// native's <c>c_sabmRescue | c_sabmRescueAny | c_sabmLand</c>
    /// (appmsg.cpp:233; the same triple at WinTrek.cpp:10225,10253 and the
    /// lifepod waypoint classifier, shipIGC.h:2126-2128).</summary>
    private const short RescueStationMask =
        DataStationTypeIGC.SabmRescue | DataStationTypeIGC.SabmRescueAny | DataStationTypeIGC.SabmLand;

    /// <summary>The eject-target capability mask for a PROBE. Native passes
    /// the SAME station mask to <c>HasCapability</c> for probes
    /// (Igc/common.cpp:329-333), so this also retains the Land bit's
    /// <c>c_eabmShootStations</c> alias. A friendly probe with any requested
    /// bit qualifies; RescueAny additionally bypasses the side gate
    /// (Igc/common.cpp:282-286).</summary>
    private const short RescueProbeMask = RescueStationMask;

    /// <summary>
    /// P540/P977 — choose the eject autopilot's target from own/allied
    /// stations and probes, plus RescueAny probes of ANY side, using native's
    /// <c>FindTarget(pShip, c_ttStation | c_ttFriendly | c_ttAnyCluster |
    /// c_ttProbe, …, c_sabmRescue | c_sabmRescueAny | c_sabmLand)</c>
    /// (appmsg.cpp:233; ally mode 1 at Igc/igc.h:4628-4644). The current
    /// cluster's strict nearest target wins; otherwise known warps are searched
    /// breadth-first and each remote cluster measures from its entry warp
    /// (Igc/common.cpp:648-729). PURE and STATIC for headless tests. (Native
    /// also passes the current
    /// command target as a tie-break preference — this client has no
    /// command-console / command-target concept, verified in P417's writeup, so
    /// that parameter has no equivalent here.)
    /// <para>P571 — should the P540 pod autopilot remain engaged? Only while
    /// the own ship is NOT docked. The moment the own ship docks (native
    /// <c>DockWithStationEvent</c> → <c>SetState(c_ssDocked)</c>,
    /// trekigc.cpp:4146) the flight/goto interface — including the eject
    /// autopilot engaged at appmsg.cpp:235 — is left behind; a freshly launched
    /// hull must start with NO autopilot, else it steers straight back to the
    /// station after death. PURE + STATIC so the dock-transition rule is
    /// headlessly testable.</para>
    /// </summary>
    internal static bool EjectAutopilotStaysEngaged(bool ownShipDocked)
        => !ownShipDocked;

    internal static AutopilotTarget? ChooseEjectRescueTarget(
        ClientWorld world, short mySideId, SimVec fromPos,
        Func<short, short> stationSabm, Func<short, short> probeEabm,
        short? fromClusterId = null)
    {
        bool Friendly(short side) => side == mySideId || world.AreAllied(mySideId, side);

        // FindableModel (Igc/common.cpp:254-342): own/allied stations and
        // probes are c_ttFriendly in ally mode 1. ONLY a RescueAny probe can
        // bypass that side gate; HasCapability is an ANY-bit test.
        AutopilotTarget? Nearest(short? clusterId, SimVec origin)
        {
            AutopilotTarget? best = null;
            float bestDistSq = float.MaxValue;
            foreach (var st in world.Stations.Values)
            {
                if (clusterId is { } cid && st.ClusterId != cid) continue;
                if (!Friendly(st.SideId)) continue;
                if ((stationSabm(st.StationTypeId) & RescueStationMask) == 0) continue;
                float d = DistSq(origin, st.Position[0], st.Position[1], st.Position[2]);
                if (d < bestDistSq)
                {
                    bestDistSq = d;
                    best = new AutopilotTarget(TargetKind.Station, st.StationId, st.Name);
                }
            }
            foreach (var pb in world.Probes.Values)
            {
                if (clusterId is { } cid && pb.ClusterId != cid) continue;
                short abilities = probeEabm(pb.ProbetypeId);
                bool rescueAny = (abilities & DataStationTypeIGC.SabmRescueAny) != 0;
                if (!rescueAny && !Friendly(pb.SideId)) continue;
                if ((abilities & RescueProbeMask) == 0) continue;
                float d = DistSq(origin, pb.Position.X, pb.Position.Y, pb.Position.Z);
                if (d < bestDistSq)
                {
                    bestDistSq = d;
                    best = new AutopilotTarget(TargetKind.Probe, pb.ProbeId, $"probe {pb.ProbeId}");
                }
            }
            return best;
        }

        // Headless callers which predate cluster context retain the original
        // all-known-target nearest query. The real EJECT path always supplies
        // its cluster and therefore takes native's ordered search below.
        if (fromClusterId is not { } startCluster)
            return Nearest(null, fromPos);

        // common.cpp:648-655: ANY current-cluster match ends the search. Raw
        // coordinates in different sectors are not globally comparable.
        if (Nearest(startCluster, fromPos) is { } local)
            return local;

        // common.cpp:658-729: walk the known warp graph breadth-first. The
        // first remote cluster with a match wins; candidates there are nearest
        // to the DESTINATION (entry) warp mouth, not to the eject position.
        var visited = new HashSet<short> { startCluster };
        var queue = new Queue<short>();
        queue.Enqueue(startCluster);
        while (queue.Count > 0)
        {
            short cluster = queue.Dequeue();
            foreach (var warp in world.Warps.Values)
            {
                if (warp.ClusterId != cluster) continue;
                if (!world.Warps.TryGetValue(warp.DestinationId, out var destination)) continue;
                if (destination.ClusterId == cluster || visited.Contains(destination.ClusterId)) continue;
                if (destination.Position is not { Length: 3 } p) continue;
                visited.Add(destination.ClusterId);
                SimVec entry = new(p[0], p[1], p[2]);
                if (Nearest(destination.ClusterId, entry) is { } remote)
                    return remote;
                queue.Enqueue(destination.ClusterId);
            }
        }
        return null;
    }

    private static float DistSq(SimVec p, float x, float y, float z)
    {
        float dx = p.X - x, dy = p.Y - y, dz = p.Z - z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>P540 — the own-ship reaction to an incoming <c>FM_S_EJECT</c>:
    /// snap the local simulation to the pod pose (so the renderer shows the
    /// pod immediately, with its native roll spin), then choose the nearest
    /// friendly rescue target and engage the P524 autopilot toward it, playing
    /// the engage sound — native's <c>SetAutoPilot(true)</c> +
    /// <c>PlayNotificationSound(salAutopilotEngageSound)</c> (appmsg.cpp:230-237).
    /// Runs under the world gate (the receive side latched
    /// <see cref="ShipEntity.HasPendingEject"/>); Monitor re-entrancy makes the
    /// nested world reads safe. A single consumer clears the latch.</summary>
    private void HandleOwnShipEject(ClientWorld world, ShipEntity mine)
    {
        if (mine.Latest is not { } up)
        {
            world.ClearOwnEjectFlag(mine.ShipId);
            return;
        }

        SeedSimFrom(up);                 // snap the local sim to the pod pose (pos/vel/fwd + pi·2 roll spin)
        world.ClearOwnEjectFlag(mine.ShipId);
        Console.WriteLine($"[eject] own ship {mine.ShipId} became lifepod hull {mine.HullId} at {up.Position}");

        var fromPos = up.Position;
        var target = ChooseEjectRescueTarget(world, world.MySideId ?? -1, fromPos,
            StationSabm, ProbeEabm, mine.SectorId);

        if (target is { } t)
        {
            EngageAutopilotToward(t, "eject: autopilot home");
            Console.WriteLine($"[eject] autopilot target chosen: {t.Name} ({t.Kind}#{t.Id}) in sector {SectorNameFor(world, t)}");
        }
        else
        {
            Console.WriteLine("[eject] no friendly rescue-capable station/probe in reach — pod drifts (no autopilot)");
        }
    }

    /// <summary>Resolve a rescue target's sector display name from the world
    /// cluster table (the autopilot target's logged sector), for the P540
    /// "autopilot target chosen (name and sector)" live-proof line.</summary>
    private static string SectorNameFor(ClientWorld world, AutopilotTarget t)
    {
        short? clusterId = t.Kind switch
        {
            TargetKind.Station => world.Stations.TryGetValue(t.Id, out var st) ? st.ClusterId : null,
            TargetKind.Probe => world.Probes.TryGetValue(t.Id, out var pb) ? pb.ClusterId : null,
            _ => null,
        };
        if (clusterId is { } cid && world.Clusters.TryGetValue(cid, out var cl))
            return $"{cl.Name} ({cid})";
        return clusterId?.ToString() ?? "unknown";
    }

    /// <summary>P540 — engage the player autopilot toward a chosen target,
    /// factoring the engage tail out of <c>ToggleAutopilotForTarget</c>
    /// (P212/P524) so the G-key and the eject rescue path share one engage
    /// (sets the selection, the engaged flag, the mouse baseline, the
    /// stationary-target snapshot, logs, plays the engage sound, refreshes the
    /// title).</summary>
    private void EngageAutopilotToward(AutopilotTarget at, string logPrefix)
    {
        _autoTarget = at;
        _autoPilotEngaged = true;
        // A new order starts its own P862 hold window (see AutopilotHoldClock).
        _autoPilotHold.Reset();
        // M-1 — capture the virtual-joystick baseline at engage (native's
        // trekJoyStick snapshot, WinTrek.cpp:8283-8289) so only a deflection
        // *past 2× deadzone from here* counts as manual input (P501).
        _autoPilotMouseBaseline = _mouseFlight.Current;
        _autoTargetAtEngage = ResolveTargetPosition(at, out var atPos) ? atPos : null;
        Console.WriteLine($"[monogame] {logPrefix} autopilot ON → {at.Name} ({at.Kind}#{at.Id})");
        _sound?.PlayAutopilotEngage();
        RefreshWindowTitle();
    }

    /// <summary>The station's <c>SabmCapabilities</c> from the client's shop
    /// catalogue (0 when unknown) — the same table <c>StationRoleBits</c> reads.</summary>
    private short StationSabm(short stationTypeId)
    {
        if (_shopCatalog?.StationTypes is not { } sts) return 0;
        return sts.TryGetValue(stationTypeId, out var st) ? st.SabmCapabilities : (short)0;
    }

    /// <summary>The probe type's <c>EabmCapabilities</c> from the client's shop
    /// catalogue (0 when unknown).</summary>
    private short ProbeEabm(short probeTypeId)
    {
        if (_shopCatalog?.ProbeTypes is not { } pts) return 0;
        return pts.TryGetValue(probeTypeId, out var p) ? p.EabmCapabilities : (short)0;
    }

    /// <summary>P540 — resolve the lifepod <c>HullId</c> for an ejected ship's
    /// side: <c>SideId → CivId</c> via the world's SIDE_ROSTER, then
    /// <c>Civilization.Lifepod</c> from the static core (native
    /// <c>GetCivilization()->GetLifepod()</c>, appmsg.cpp:216). Falls back to
    /// the first hull in the core carrying the <c>c_habmLifepod</c> capability
    /// when the roster/civilization is unresolved (robust regardless of side),
    /// and null when no core is loaded — the world then skips the hull swap.
    /// Runs on the receive thread (inside <c>ClientWorld.Apply(Eject)</c>) and
    /// reads only immutable catalogue tables plus the receive-thread world, so
    /// it needs no locking.</summary>
    internal short? ResolveLifepodHull(short sideId)
    {
        var cat = _shopCatalog;
        if (cat?.Core?.Civilizations is { } civs)
        {
            short? civId = null;
            if (_session.World.SideRoster is { } sr)
            {
                foreach (var s in sr.Sides)
                {
                    if (s.SideId == sideId) { civId = s.CivId; break; }
                }
            }
            if (civId is { } cid && civs.TryGetValue(cid, out var civ) && civ.Lifepod != -1)
                return civ.Lifepod;
        }
        return FallbackLifepodHull(cat);
    }

    private static short? FallbackLifepodHull(ShopCatalog? cat)
    {
        if (cat is null) return null;
        foreach (var h in cat.Hulls)
            if (h.HasCapability(DataHullTypeIGC.HabmLifepod)) return h.HullId;
        return null;
    }
}
