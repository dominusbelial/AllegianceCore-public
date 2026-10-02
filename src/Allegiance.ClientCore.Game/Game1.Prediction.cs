using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame;

// P447b — the own-ship predictor wiring. Split into its own partial-class file so
// the per-frame bookkeeping (feed-on-new-update, hull re-config, hard-transition
// freeze/resume, periodic [predict] diagnostics) stays together and clear.
//
// Lifecycle (all on the main thread; the "gate" is _session.Gate, the same lock the
// receive thread upserts world.Ships under, so reading ShipEntity.Latest /
// LastUpdateTick / LatestArrivalTime / SectorId here is race-safe):
//   1. Each frame AFTER the command that's sent through CommandControls (manual or
//      autopilot) is chosen, Game1.FlightInput calls _ownPredict.Step(frameDt,
//      sentControls, sentStateM) with EXACTLY what was sent.
//   2. Under the gate in the pose gather, UpdateOwnShipPrediction resolves the hull,
//      feeds each newly-arrived authoritative update once, and applies the hard-
//      transition freeze/resume edges.
//   3. The render pose / camera / HUD read the predicted pose via _ownPredict.
public sealed partial class Game1
{
    /// <summary>
    /// P447b — drive the own-ship predictor from the real client world for this frame.
    /// MUST be called under <c>_session.Gate</c> (with the own ship's
    /// <paramref name="mine"/> and its current <paramref name="up"/>), immediately
    /// before the render pose is read, so the predictor is fed the latest
    /// authoritative state and any hard-transition freeze/resume is applied first.
    /// Steps:
    /// <list type="number">
    /// <item>Reconfigure the hull/afterburner when it first becomes known or changes
    /// (respawn / lifepod / hull swap), so the integrator uses the real stats.</item>
    /// <item>Feed each newly-arrived authoritative update exactly once (detected via
    /// <see cref="ShipEntity.LastUpdateTick"/>).</item>
    /// <item>Apply the hard-transition freeze/resume edges (dock, death, ripcord,
    /// warp/sector change), so prediction holds the server pose across a
    /// discontinuity and re-seeds from the next server pose once it settles.</item>
    /// </list>
    /// </summary>
    private void UpdateOwnShipPrediction(ClientWorld world, ShipEntity mine, ServerShipUpdate up)
    {
        if (!_ownShipPrediction) return;   // kill switch: prediction fully off (raw poses)

        // 1 — hull/afterburner: (re)configure only when the hull identity changes. The
        // predictor derives afterburner POWER from each authoritative update, so a
        // per-frame SetHull would zero it; a change-only SetHull preserves it.
        if (TryResolveOwnPredictionHull(world, mine) is { } rs &&
            (mine.HullId != _ownPredictHullId || rs.AfterburnerMaxThrust != _ownPredictAbThrust))
        {
            _ownPredict.SetHull(rs.Stats, rs.AfterburnerMaxThrust);
            _ownPredictHullId = mine.HullId;
            _ownPredictAbThrust = rs.AfterburnerMaxThrust;
        }

        // 2 — feed each newly-arrived authoritative update exactly once, anchored at
        // its local arrival time and extrapolated to now on the same monotonic clock.
        if (!_ownPredictHasFed || mine.LastUpdateTick != _ownPredictFedTick)
        {
            _ownPredict.FeedUpdate(up, mine.LatestArrivalTime, world.NowSeconds());
            _ownPredictFedTick = mine.LastUpdateTick;
            _ownPredictHasFed = true;
        }

        // 3 — hard-transition freeze/resume. Each discrete discontinuity freezes
        // prediction to the held server pose; once the condition clears, Resume re-arms
        // it and the next server pose re-seeds (the snap inside OnServerUpdate also
        // covers any missed signal, per the P447a >20 m guarantee).
        if (_ownPredictLastSectorId == -1)
            _ownPredictLastSectorId = mine.SectorId;   // first sight: not a transition
        bool sectorChanged = mine.SectorId != _ownPredictLastSectorId;
        if (sectorChanged) _ownPredictLastSectorId = mine.SectorId;   // latch the new sector

        bool freeze =
            mine.StationId != -1        // docked (own ship off the flight stream; hangar screen)
            || mine.IsNotVisible        // dead / gone (pose meaningless until respawn)
            || world.RipcordActive      // ripcord pins turn rates + moves the ship
            || sectorChanged;           // warp / sector change / teleport discontinuity

        if (freeze)
        {
            if (!_ownPredict.Frozen)
            {
                // Snap to the authoritative pose and hold it across the transition.
                _ownPredict.HardReset(
                    up.Position, up.Velocity, up.Orientation, up.TurnRates);
                Console.WriteLine("[predict] hard reset (freeze): " +
                    (mine.StationId != -1 ? "docked"
                     : mine.IsNotVisible ? "dead"
                     : world.RipcordActive ? "ripcord"
                     : "warp/sector-change/teleport"));
            }
        }
        else if (_ownPredict.Frozen)
        {
            _ownPredict.Resume();
            Console.WriteLine("[predict] resume (transition settled; re-seeding from server pose)");
        }
    }

    /// <summary>
    /// P447b — resolve the own ship's already-effective hull stats (hull record × the
    /// local side's global-attribute factors, identity when the side is unknown) plus
    /// the mounted afterburner's max thrust (from the own ship's Loadout, resolved via
    /// the shop catalogue — 0 when absent or unresolvable). Mirrors the P448b remote
    /// resolver but reads the OWN loadout so the prediction boosts realistically under
    /// the afterburner. Returns null when the hull record isn't in the (possibly
    /// absent) catalogue yet — the predictor then waits for a later update.
    /// </summary>
    private (ShipDeadReckoner.HullStats Stats, float AfterburnerMaxThrust)? TryResolveOwnPredictionHull(ClientWorld world, ShipEntity mine)
    {
        var cat = _shopCatalog;
        if (cat is null) return null;
        DataHullTypeIGC? hull = null;
        foreach (var h in cat.Hulls)
            if (h.HullId == mine.HullId) { hull = h; break; }
        if (hull is null) return null;

        var gas = world.GlobalAttributesFor(world.MySideId ?? -1);
        var stats = ShipDeadReckoner.HullStats.FromHull(
            hull.Value,
            gas.Get(Allegiance.Core.Model.GlobalAttributeSet.Thrust),
            gas.Get(Allegiance.Core.Model.GlobalAttributeSet.MaxSpeed),
            gas.Get(Allegiance.Core.Model.GlobalAttributeSet.TurnRate),
            gas.Get(Allegiance.Core.Model.GlobalAttributeSet.TurnTorque));

        // The own ship's mounted afterburner max thrust: scan the real Loadout
        // (BUY_LOADOUT_ACK-granted) for an Afterburner part and use its MaxThrust;
        // 0 = absent / unresolvable (behaviourally identical to no afterburner).
        float abMax = 0f;
        if (mine.Loadout is { Count: > 0 } lo)
        {
            foreach (var p in lo)
            {
                foreach (var pt in cat.Parts)
                {
                    if (pt.PartId != p.PartId) continue;
                    if (pt.Type is DataPartTypeIGC.Afterburner ab) { abMax = ab.MaxThrust; }
                    break;
                }
            }
        }
        return (stats, abMax);
    }

    /// <summary>
    /// P447b — periodically log the live correction diagnostics so a live session can
    /// be diagnosed (item 4): mean/max per-update reconciliation distance and the
    /// cumulative snap count, polled (and reset) at most once every ~2 s under the
    /// same gate as the pose gather, mirroring the P448b remote-correction log.
    /// </summary>
    private void LogOwnPredictionDiagnostics()
    {
        if (!_ownShipPrediction) return;
        if (_nowSeconds - _ownPredictLogAt < 2.0) return;
        _ownPredictLogAt = _nowSeconds;
        var (cnt, mean, mx) = _ownPredict.Predictor.TakeCorrectionStats();
        if (cnt > 0)
            Console.WriteLine($"[predict] correction: n={cnt} mean={mean:0.000}m max={mx:0.000}m snaps(total)={_ownPredict.Predictor.SnapCount}");
    }
}
