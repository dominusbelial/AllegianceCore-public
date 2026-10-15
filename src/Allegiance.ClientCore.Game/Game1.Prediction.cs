using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame;

// P508 — the own-ship simulation wiring (replaces the P447b predictor wiring).
// Split into its own partial-class file so the per-frame bookkeeping (server-clock
// feed, hull re-config, first-sight seed, SHIP_RESET re-seed, hard-transition
// freeze/resume) stays together and clear.
//
// Native's own-ship model: the client FLIES ITS OWN SHIP LOCALLY every frame with
// the port's flight step (ShipKinematics, P448a) and the side-GA-scaled hull stats,
// and tells the server where it is (P507 server sanity-checks + SHIP_RESET). The
// rendered own-ship pose IS that local simulation, at full frame rate, with NO
// reconciliation / blending / snap (the P447/P502/P505 OwnShipPredictor is retired).
// Divergence is corrected only by the server's real SHIP_RESET, which snaps the sim
// to the server pose + a new cookie.
//
// Lifecycle (all on the main thread; the "gate" is _session.Gate, the same lock the
// receive thread upserts world.Ships under, so reading ShipEntity.Latest /
// LastUpdateTick / Cookie / HasPendingReset here is race-safe):
//   1. Each frame this runs UNDER THE GATE (right before the render pose is read):
//      feed the server clock, (re)configure the hull, seed the sim at first sight,
//      apply any received SHIP_RESET (snap + new cookie), and the hard-transition
//      freeze/resume edges. The sim itself is STEPPED in FlightInput with the exact
//      controls sent that frame (also on the main thread, outside the gate).
//   2. The render pose / camera / HUD read the simulated pose via _ownSim.
//   3. At the 0.15 s cadence, FlightInput sends a native SHIP_UPDATE from the sim
//      pose + _ownShipCookie + _ownServerClock time.
public sealed partial class Game1
{
    private Allegiance.Core.Wire.ExpandedPartData? _ownBoostPart;
    private short _ownBoostHull = -1;

    private void ConfigureOwnBooster(ShipEntity mine, ServerShipUpdate update)
    {
        Allegiance.Core.Wire.ExpandedPartData? mounted = null;
        foreach (var part in mine.Loadout ?? [])
            if (ResolveFlightPart(part.PartId)?.EquipmentType == EquipmentType.Afterburner) { mounted = part; break; }
        if (_ownBoostHull == mine.HullId && mounted == _ownBoostPart && !mine.HasPendingReset) return;
        _ownBoostPart = mounted;
        _ownBoostHull = mine.HullId;
        var hull = _shopCatalog?.Hulls.FirstOrDefault(h => h.HullId == mine.HullId);
        var constants = _shopCatalog?.Core?.Constants.FloatConstants;
        float rate = constants is { Length: > 10 } ? constants[10] : 1;
        var type = mounted is { } p ? ResolveFlightPart(p.PartId)?.Type as DataPartTypeIGC.Afterburner : null;
        _ownSim.SetAfterburner(type, mounted?.FractionMounted ?? 255, update.FuelFraction * (hull?.MaxFuel ?? 0), rate, mine.HasPendingReset ? update.Power : 0,
            thrustFactor: _session.World.GlobalAttributesFor(_session.World.MySideId ?? -1).Get(GlobalAttributeSet.Thrust));
    }
    /// <summary>
    /// P508 — drive the own-ship local simulation from the real client world for
    /// this frame. MUST be called under <c>_session.Gate</c> (with the own ship's
    /// <paramref name="mine"/> and its current <paramref name="up"/>), immediately
    /// before the render pose is read, so the sim is seeded from the latest
    /// authoritative state and any hard-transition freeze/resume / SHIP_RESET is
    /// applied first. Steps:
    /// <list type="number">
    /// <item>Feed the own-ship server clock from the pose's tick slot (so a native
    /// SHIP_UPDATE can be stamped with server-converted time).</item>
    /// <item>Reconfigure the hull/afterburner when it first becomes known or changes
    /// (respawn / lifepod / hull swap), so the simulation uses the real stats.</item>
    /// <item>Seed the simulation from the server pose at first sight, adopting the
    /// ship cookie.</item>
    /// <item>Apply a received <c>SHIP_RESET</c>: snap the simulation to the server's
    /// authoritative pose and adopt the new cookie (the server ignores further
    /// updates until we echo it).</item>
    /// <item>Apply the hard-transition freeze/resume edges (dock, death, ripcord,
    /// warp / sector change), seeding the sim to the server pose across each
    /// discontinuity and holding it until it settles.</item>
    /// </list>
    /// </summary>
    private void UpdateOwnShipSimulation(ClientWorld world, ShipEntity mine, ServerShipUpdate up)
    {
        // 1 — (the server clock is fed from FM_CS_PING echoes in PumpServerClock, not
        //    from the pose tick slot — that slot is a loop counter, not seconds).

        // 2 — hull/afterburner: (re)configure only when the hull identity changes.
        if (TryResolveOwnSimulationHull(world, mine) is { } rs &&
            (mine.HullId != _ownSimHullId || rs.AfterburnerMaxThrust != _ownSimAbThrust))
        {
            _ownSim.SetHull(rs.Stats, rs.AfterburnerMaxThrust);
            _ownSimHullId = mine.HullId;
            _ownSimAbThrust = rs.AfterburnerMaxThrust;
            if (_ownSimSeeded)
                Console.WriteLine("[sim] hull reconfigured (respawn/lifepod/hull-swap)");
            // P522 — a hull change (buy-a-new-hull at the hangar, respawn,
            // lifepod) legitimately changes the hull/shield fractions the wire
            // reports (new hull's starting shield vs the old hull's full one);
            // that is a transition, not damage — abandon the flash baseline so
            // the change never false-arms the red damage flash.
            _flashTracker.ResetBaseline();
        }

        ConfigureOwnBooster(mine, up);

        // Adopt the current ship cookie every frame (pick up SET_CLUSTER at join and
        // warp, and SHIP_RESET renewals) — the send path echoes this.
        _ownShipCookie = mine.Cookie;

        // 3 — first sight: seed the simulation from the server pose and start flying.
        if (!_ownSimSeeded)
        {
            SeedSimFrom(up);
            _ownSimSeeded = true;
            Console.WriteLine("[sim] seeded from server pose " + up.Position);
        }

        // 4 — SHIP_RESET: the server rejected our pose; snap to its authoritative pose
        // and adopt the new cookie (updates are ignored until we echo it).
        if (mine.HasPendingReset)
        {
            SeedSimFrom(up);
            world.ClearOwnShipResetFlag(mine.ShipId);
            _shipResetCount++;
            Console.WriteLine($"[sim] SHIP_RESET #{_shipResetCount} — snapped to server pose, cookie 0x{mine.Cookie:X2}");
            // P522 — the reset snap legitimately jumps the wire pose/health
            // (and rotates the cookie); that is a correction, not damage, so
            // abandon the flash baseline so the snap never reads as a hit.
            _flashTracker.ResetBaseline();
        }

        // 4.5 — EJECT (P540): the server broadcast our ship turning into a
        // drifting Lifepod hull. Snap the sim to the pod pose (hull reconfigure
        // in step 2 already adopted the lifepod's stats; this re-seeds the pose
        // + native roll spin) and engage the rescue autopilot toward the nearest
        // friendly station/probe. The pod is alive and in flight, so the freeze
        // logic below leaves it unfrozen.
        if (mine.HasPendingEject)
            HandleOwnShipEject(world, mine);

        // 5 — hard-transition freeze/resume. Each discrete discontinuity seeds the sim
        // to the held server pose and freezes it; once the condition clears, Resume
        // re-arms it and the sim continues from that (server) pose.
        if (_ownSimLastSectorId == -1)
            _ownSimLastSectorId = mine.SectorId;   // first sight: not a transition
        bool sectorChanged = mine.SectorId != _ownSimLastSectorId;
        if (sectorChanged) _ownSimLastSectorId = mine.SectorId;   // latch the new sector

        bool freeze =
            mine.StationId != -1        // docked (own ship off the flight stream; hangar screen)
            || mine.IsNotVisible        // dead / gone (pose meaningless until respawn)
            || world.RipcordActive      // ripcord pins turn rates + moves the ship
            || sectorChanged;           // warp / sector change / teleport discontinuity

        if (freeze)
        {
            if (!_ownSim.Frozen)
            {
                SeedSimFrom(up);         // hold the authoritative pose across the transition
                _ownSim.Freeze();
                // P522 — remember the last server update tick at the freeze, so
                // the resume edge can tell when a FRESH authoritative update
                // (the post-undock SET_CLUSTER launch pose) has arrived and
                // re-seed from it instead of the stale pre-transition pose.
                _ownSimFrozenAtTick = mine.LastUpdateTick;
                Console.WriteLine("[sim] freeze: " +
                    (mine.StationId != -1 ? "docked"
                     : mine.IsNotVisible ? "dead"
                     : world.RipcordActive ? "ripcord"
                     : "warp/sector-change/teleport"));
                // P522 — dock/death are transitions, not damage; abandon the
                // flash baseline (a docked ship's held fractions differ from
                // the pre-dock ones only by a legitimate transition).
                _flashTracker.ResetBaseline();
            }
        }
        else if (_ownSim.Frozen)
        {
            // P522 — resume from the server's CURRENT authoritative pose, never
            // the stale pre-transition pose. On undock/launch the server places
            // the ship at the station's real launch point and sends SET_CLUSTER
            // (Server.PerformUndock, Server.Docking.cs) which updates `up`; on
            // warp/sector-change/respawn it similarly sends the post-jump pose.
            // Resuming from the frozen pre-transition pose instead sent the
            // first native SHIP_UPDATE ~700 m off the launch point, got it
            // rejected, and SHIP_RESET teleported the ship on every launch —
            // the recurring undock SHIP_RESET (3 consecutive launches in the
            // live log). Gate the re-seed on a FRESH server update having
            // arrived since the freeze (mine.LastUpdateTick != the tick
            // recorded at freeze), so we never resume from a stale pose even
            // when the undock's SHIP_STATUS(NA) clears StationId a frame before
            // the launch SET_CLUSTER lands. Re-seeding then resuming guarantees
            // the first post-transition SHIP_UPDATE matches the server pose →
            // zero rejects, zero SHIP_RESETs.
            if (mine.LastUpdateTick != _ownSimFrozenAtTick)
            {
                SeedSimFrom(up);
                _ownSim.Resume();
                Console.WriteLine("[sim] resume (transition settled; re-seeded from server pose)");
                // P522 — undock/launch is a transition, not damage; abandon the
                // flash baseline so the launch's new-hull values never false-
                // arm the red damage flash.
                _flashTracker.ResetBaseline();
            }
            // else: no fresh authoritative update yet — stay frozen this frame.
        }
    }

    /// <summary>Seed the local simulation from a server pose and resume flying.</summary>
    private void SeedSimFrom(ServerShipUpdate up)
        => _ownSim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates);

    /// <summary>
    /// P508 — feed the own-ship server clock from received FM_CS_PING echoes and send
    /// a ~1 Hz ping while connected. Called every frame from <c>Game1.Update</c> (any
    /// state) so a server-converted SHIP_UPDATE time is ready by undock. The server
    /// stamps <c>timeServer = SimNow·1000</c> on each echo, so the clock needs no
    /// tick-rate assumption (see <see cref="Flight.OwnShipServerClock"/>).
    /// </summary>
    private void PumpServerClock()
    {
        // P508 — feed the own-ship server clock from received FM_CS_PING echoes.
        // P671 — the SESSION's native keep-alive now sends the CS_PING (every
        // 6 s, every state — see ConnectionSession), so this pump no longer sends
        // its own ~1 Hz ping; sending both would double-ping. It only CONSUMES
        // the echoes, which arrive for whichever of the session's pings the
        // server answered. The clock extrapolates at ~1:1 (OwnShipServerClock),
        // so a 6 s sample cadence stays well inside the server's ±1.5 s gate —
        // exactly the cadence the native client's CheckServerLag runs at.
        foreach (var (serverMs, localMs) in _session.TakePingEchoes())
            _ownServerClock.AddSample(serverMs, localMs);
    }

    /// <summary>
    /// P508 — emit the native <c>FM_C_SHIP_UPDATE(24)</c> at the native 0.15 s
    /// cadence (<c>c_dsecUpdateClient</c>) from the local simulation: server-converted
    /// time, the current ship cookie, and the full pose (position / orientation /
    /// velocity / turn rates / power) plus the controls/stateM the sim is flying with.
    /// Runs after the pose gather (clock fed, cookie adopted, SHIP_RESET applied) on
    /// the main thread. No-op while the ship is frozen (docked / dead / ripcord /
    /// warp), not armed (turret gunner), unseeded, or before a server-clock estimate
    /// exists (pre-flight; controls flow via the mission start only once flying).
    /// Matches native: no send-on-control-change (<c>smSendUpdateNow</c> is commented
    /// out natively) — only the cadence, each emission carrying the controls in effect
    /// at that instant.
    /// </summary>
    private void MaybeSendOwnShipUpdate()
    {
        // P518 — compute send ELIGIBILITY and log the stop/resume edges (with the
        // reason) so a silent "client stopped sending" is never invisible again.
        string? blockedReason = null;
        if (!_ownShipSendArmed) blockedReason = "not armed (turret gunner)";
        else if (!_ownSim.HaveHull) blockedReason = "no hull resolved yet";
        else if (_ownSim.Frozen) blockedReason = "sim frozen (docked/dead/ripcord/warp)";
        else if (!_ownServerClock.Ready) blockedReason = "no server-clock estimate yet";

        bool eligible = blockedReason is null;
        if (eligible != _ownShipEligible)
        {
            _ownShipEligible = eligible;
            Console.WriteLine(eligible
                ? "[sim] RESUMED sending native SHIP_UPDATE"
                : $"[sim] STOPPED sending native SHIP_UPDATE: {blockedReason}");
        }
        if (!eligible) return;

        if (!_shipSendCadence.Update(_nowSeconds)) return;

        uint t = _ownServerClock.ServerTimeMs(Environment.TickCount64);
        var payload = ClientShipUpdate.FromPose(
            t,
            _ownSim.Position, _ownSim.Velocity, _ownSim.Orientation,
            _ownSim.StateM, _ownSim.Controls, _ownSim.TurnRates,
            _ownSim.Power);
        _session.SendShipPose(t, _ownShipCookie, payload);
    }

    /// <summary>
    /// P508 — resolve the own ship's already-effective hull stats (hull record × the
    /// local side's global-attribute factors, identity when the side is unknown) plus
    /// the mounted afterburner's max thrust (from the own ship's Loadout, resolved via
    /// the shop catalogue — 0 when absent or unresolvable). Mirrors the P448b remote
    /// resolver but reads the OWN loadout so the simulation boosts realistically under
    /// the afterburner. Returns null when the hull record isn't in the (possibly
    /// absent) catalogue yet — the simulation then waits for a later update.
    /// </summary>
    private (ShipDeadReckoner.HullStats Stats, float AfterburnerMaxThrust)? TryResolveOwnSimulationHull(ClientWorld world, ShipEntity mine)
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
                    if (pt.Type is DataPartTypeIGC.Afterburner ab) { abMax = ab.MaxThrust * gas.Get(GlobalAttributeSet.Thrust); }
                    break;
                }
            }
        }
        return (stats, abMax);
    }
}
