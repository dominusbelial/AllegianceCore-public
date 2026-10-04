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

    /// <summary>The eject-target capability mask for a PROBE, mirroring the
    /// expendable twin <c>c_eabmRescue | c_eabmRescueAny</c> (igc.h:781-782 —
    /// both aliases of <c>c_sabmRescue</c>/<c>c_sabmRescueAny</c>), the probe
    /// rescue bits a lifepod can drift into and be rescued by.</summary>
    private const short RescueProbeMask =
        DataStationTypeIGC.SabmRescue | DataStationTypeIGC.SabmRescueAny;

    /// <summary>
    /// P540 — choose the eject autopilot's target: the nearest FRIENDLY station
    /// or probe, in ANY cluster, whose capabilities include rescue/land
    /// (native's <c>FindTarget(pShip, c_ttStation | c_ttFriendly | c_ttAnyCluster |
    /// c_ttProbe, …, c_sabmRescue | c_sabmRescueAny | c_sabmLand)</c>,
    /// appmsg.cpp:233). Enemies and non-rescue-capable stations are ignored;
    /// distance is measured from the eject position. PURE and STATIC so the
    /// target pick is unit-testable headlessly. (Native also passes the current
    /// command target as a tie-break preference — this client has no
    /// command-console / command-target concept, verified in P417's writeup, so
    /// that parameter has no equivalent here.)
    /// </summary>
    internal static AutopilotTarget? ChooseEjectRescueTarget(
        ClientWorld world, short mySideId, SimVec fromPos,
        Func<short, short> stationSabm, Func<short, short> probeEabm)
    {
        AutopilotTarget? best = null;
        float bestDistSq = float.MaxValue;

        // Stations — friendly + rescue/land-capable, any cluster (world.Stations
        // spans every known sector).
        foreach (var st in world.Stations.Values)
        {
            if (st.SideId != mySideId) continue;                                   // c_ttFriendly
            if ((stationSabm(st.StationTypeId) & RescueStationMask) == 0) continue;
            float d = DistSq(fromPos, st.Position[0], st.Position[1], st.Position[2]);
            if (d < bestDistSq)
            {
                bestDistSq = d;
                best = new AutopilotTarget(TargetKind.Station, st.StationId, st.Name);
            }
        }

        // Probes — friendly + rescue-capable, any cluster.
        foreach (var pb in world.Probes.Values)
        {
            if (pb.SideId != mySideId) continue;                                   // c_ttFriendly
            if ((probeEabm(pb.ProbetypeId) & RescueProbeMask) == 0) continue;
            float d = DistSq(fromPos, pb.Position.X, pb.Position.Y, pb.Position.Z);
            if (d < bestDistSq)
            {
                bestDistSq = d;
                best = new AutopilotTarget(TargetKind.Probe, pb.ProbeId, $"probe {pb.ProbeId}");
            }
        }

        return best;
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
            StationSabm, ProbeEabm);

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
