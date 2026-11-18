using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.Flight;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    /// <summary>P575 — per-station-type landing-bay geometry cache for the
    /// player dock autopilot (mirrors the server's <c>StationLandingBayCache</c>
    /// shape, resolved from the type's real <c>.cvh</c>). Keyed by
    /// <c>StationTypeId</c>; null = no bays.</summary>
    private readonly Dictionary<short, Allegiance.Core.Collision.HullLandingBayGeometry?> _dockBaysByStationType = new();

    /// <summary>P575 — cached station landing-bay geometry from the type's real
    /// <c>.cvh</c> under the artwork root via the SHARED reconstructor (the same
    /// source the server's dock gate consumes), keyed by <c>StationTypeId</c>.
    /// Any failure degrades to null → the autopilot falls through to the plain
    /// centre/goto (native no-bay power glide), never a crash — the client's
    /// established degrade-not-fail asset convention (MuzzleOffsetsFor).</summary>
    private Allegiance.Core.Collision.HullLandingBayGeometry? StationDockGeometry(DataStationTypeIGC type)
    {
        if (_dockBaysByStationType.TryGetValue(type.StationTypeId, out var cached)) return cached;
        Allegiance.Core.Collision.HullLandingBayGeometry? g = null;
        try
        {
            var rel = type.ModelName + ".cvh";
            if (_assets.Exists(rel))
            {
                var model = Allegiance.Core.Collision.CvhReader.Parse(Assets.AssetSources.ReadAllText(_assets, rel));
                var rec = Allegiance.Core.Collision.HullLandingBay.ReconstructStation(model, type.Radius);
                g = rec.LandSlots.Count == 0 ? null : rec;
            }
        }
        catch { g = null; }
        _dockBaysByStationType[type.StationTypeId] = g;
        return g;
    }

    /// <summary>P575 — is the own ship physically docked (the server's real
    /// docked latch, <c>ShipEntity.StationId != -1</c>)? The terminal for the
    /// dock-approach autopilot (it has no arrival-radius disengage).</summary>
    private bool IsOwnShipDocked()
    {
        lock (_session.Gate)
        {
            if (_session.World.MyShipId is { } me && _session.World.Ships.TryGetValue(me, out var ent))
                return ent.StationId != -1;
        }
        return false;
    }

    /// <summary>
    /// P575 — compute the shared native bay-approach <see cref="ControlData"/>
    /// for a same-sector STATION autopilot target, mirroring the server NPC path
    /// (<c>Ship.TryDockApproach</c>): resolve the own hull's dockability
    /// (<c>SabmLand</c>/<c>SabmCapLand</c>), the station's real bay geometry, the
    /// best bay and entry phase via <see cref="DockApproach"/>, then steer.
    /// Returns <c>false</c> when the target is not a friendly same-side dockable
    /// station with bays → the caller uses the plain centre/goto (native no-bay
    /// "power glide in").
    /// </summary>
    private bool TryComputeDockSteer(AutopilotTarget tgt, in SimVec pos, in Orientation orient, in SimVec vel, out ControlData controls)
    {
        controls = ControlData.Neutral;
        if (_shopCatalog is not { } cat) return false;

        lock (_session.Gate)
        {
            if (_session.World.MyShipId is not { } me) return false;
            if (!_session.World.Ships.TryGetValue(me, out var mine)) return false;
            if (!_session.World.Stations.TryGetValue(tgt.Id, out var station)) return false;

            // Native admits own-side AND allied stations to the InGarage dock
            // branch (Igc/shipIGC.cpp:732-735,843-855). P977: an allied eject
            // target must use the same bay approach as an own-side target;
            // exact-side-only steering made the pod grind against the hull.
            if (_session.World.MySideId is { } mySide && station.SideId != mySide
                && !_session.World.AreAllied(mySide, station.SideId)) return false;

            DataHullTypeIGC? ownHull = null;
            foreach (var h in cat.Hulls)
                if (h.HullId == mine.HullId) { ownHull = h; break; }
            if (ownHull is not { } hull) return false;

            if (cat.StationTypes is not { } stationTypes) return false;
            if (!stationTypes.TryGetValue(station.StationTypeId, out var stype)) return false;

            // Ability gate — same need the server's dock dispatch applies
            // (Sector.cs:3563-3566): fighters SabmLand, others SabmCapLand.
            short need = hull.HasCapability(DataHullTypeIGC.HabmFighter)
                ? DataStationTypeIGC.SabmLand
                : DataStationTypeIGC.SabmCapLand;
            if (!stype.HasCapability(need)) return false;

            var geom = StationDockGeometry(stype);
            if (geom is null || geom.LandSlots.Count == 0) return false;

            var so = new Orientation();
            if (station.Forward is { Length: 3 } f && station.Up is { Length: 3 } u)
                so.Set(Vec(f), Vec(u));
            SimVec center = Vec(station.Position);

            // World-rotated bays from the landing slots (capital prefix, then fighters)
            // (native centers[i] = GetLandPosition(i,0)*itsOrientation).
            var bays = new List<DockApproach.DockBay>(geom.LandSlots.Count);
            foreach (var slot in geom.LandSlots)
            {
                var p0 = slot.Planes[0];
                bays.Add(new DockApproach.DockBay(
                    p0.Position * so,
                    p0.Direction.Normalize() * so));
            }
            int nLand = DockApproach.StationBaysForClass(
                hull.HasCapability(DataHullTypeIGC.HabmFighter), bays, geom.CapLandSlots);

            float speed = vel.Length();
            var attributes = _session.World.GlobalAttributesFor(_session.World.MySideId ?? -1);
            float distanceRest = DockApproach.StoppingDistance(speed, hull.Thrust * attributes.Get(GlobalAttributeSet.Thrust),
                hull.Mass, hull.Speed * attributes.Get(GlobalAttributeSet.MaxSpeed), hull.BackMultiplier);
            SimVec positionRest = speed > .1f ? pos + vel * (distanceRest / speed) : pos;

            float shipRadius = 0.5f * hull.Length;
            float totalRadius = shipRadius + stype.Radius + 10f;

            // Native waits for angular motion to settle before entering
            // (Igc/common.cpp:1301). Prediction supplies the current local rates;
            // otherwise use the same rates carried by the latest server update.
            var rates = _ownShipPrediction && _ownSim.HaveHull
                ? _ownSim.TurnRates : mine.Latest?.TurnRates;
            var step = DockApproach.DockStation(
                pos, vel, orient, center, bays, nLand, distanceRest, positionRest,
                totalRadius, rates?[ControlData.AxisYaw] ?? 0f, rates?[ControlData.AxisPitch] ?? 0f, experimental: false,
                bayPreference: hull.HasCapability(DataHullTypeIGC.HabmLifepod) ? mine.ShipId : 0);

            // Match StepOwnShipSimulation: native angular control uses sim
            // seconds, including the server's time-dilation factor.
            float simDt = (_autopilotDeltaSeconds > 0 ? _autopilotDeltaSeconds : .033f)
                * (float)_session.World.TimeScale;
            controls = DockApproach.Steer(step, pos, orient, vel, distanceRest,
                dt: simDt, turnState: DockApproach.TurnState.FromHull(hull, attributes,
                    rates?[ControlData.AxisYaw] ?? 0f, rates?[ControlData.AxisPitch] ?? 0f));
            _autopilotMovementButtons = DockApproach.MovementButtons(step, pos, orient, vel,
                DockApproach.TurnState.FromHull(hull, attributes,
                    rates?[ControlData.AxisYaw] ?? 0f, rates?[ControlData.AxisPitch] ?? 0f));
            return true;
        }
    }

}
