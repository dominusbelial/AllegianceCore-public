using System;
using System.Collections.Generic;
using System.Globalization;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

// P1066: desktop orchestration extracted verbatim; shared primitives are NativeScene.
// Browser owns its cadence/visibility and calls the same primitives. B2/B3 can
// add adapters here and GPU-free preparation to NativeScene without sharing a host.
#if !BROWSER
public sealed partial class Game1
{
    /// <summary>P367 — per-ship inputs that drive one frame of its navigation
    /// strobe lights: the visible ship's real position/orientation and its real
    /// rendered radius (0.5 × HullType.Length). Unlike the thrust-driven effects,
    /// strobes are always blinking, so no thrust/throttle input is needed.</summary>
    private readonly record struct NavStrobeEmitter(
        Allegiance.Core.Collision.Vector3 Position,
        Allegiance.Core.Sim.Orientation Orientation,
        float Radius);

    /// <summary>P367 — gather one visible ship's strobe emitter for this frame
    /// (native LightsGeo's per-ship scope: every visible ship the client renders,
    /// not just the local hull). Runs under the Gate lock in the pose-gather pass;
    /// the emitter is recorded whenever the hull is known and visible. The local
    /// ship is gated to non-cockpit camera modes (its hull isn't drawn there),
    /// matching P358/P365. P448b — the emitter is fed the ship's RENDER pose
    /// (dead-reckoned for remote ships) so the strobes never detach.</summary>
    private void UpdateNavStrobes(short id, Allegiance.Core.Collision.Vector3 position,
        Allegiance.Core.Sim.Orientation orientation, float radius, bool visible)
    {
        if (!visible || radius <= 0f)
        {
            _navStrobeEmitters.Remove(id);
            return;
        }
        _navStrobeEmitters[id] = new NavStrobeEmitter(position, orientation, radius);
    }

    /// <summary>
    /// P576 — draw the native per-sector sky (or the pre-P576 parallax starfield
    /// fallback before a cluster is known), switching the sky assets on sector
    /// change. The sky is the backdrop, drawn behind everything (the sky renderer
    /// pins its own depth/blend states). Also draws the optional aleph
    /// destination-glimpse when <c>--aleph-glimpse on</c> (default ON; off disables it).
    /// <para><b>P862 — only the ACTIVE tier resolves the new sector.</b> All three
    /// tiers used to be told about every sector change, and each owns its own
    /// <see cref="Rendering.PlanetPoster"/> plus its own dome/environment cache —
    /// so one warp decoded the same 2048² <c>nebplntNNbmp.mdl</c> planet THREE
    /// times and the inactive tiers' nebula art on top of that, all inside the
    /// transit frame. The tier is fixed at launch (<c>--sky</c>), so the two idle
    /// tiers now stay cold; a per-tier latch re-resolves the past sector if a tier
    /// ever becomes active.</para>
    /// </summary>
    private void DrawSkyP576(Matrix view, Matrix projection, Vector3 cameraEye)
    {
        var world = _session.World;
        DataClusterIGC? sky = null;
        short skySectorId = -1;
        if (TrySceneSector(world, RenderShipId(world) ?? -1, out short skySector)
            && world.Clusters.TryGetValue(skySector, out var dc))
        {
            sky = dc;
            skySectorId = skySector;
        }

        long spaceIdentity = ((long)skySectorId << 32) ^ ((long)_cameraMode << 16)
            ^ (string.Equals(_skyTier, "black", StringComparison.OrdinalIgnoreCase) ? 1L
                : string.Equals(_skyTier, "hd", StringComparison.OrdinalIgnoreCase) ? 2L : 3L);
        _spaceVisualFrame = _spaceMotion.Step(_visuals, view, cameraEye, _nowSeconds, spaceIdentity);

        // P588 — the sky tier dispatch: black (default: point stars + native
        // planet, no dome) | native (P576 dome + planet) | hd (P577 telescope).
        bool black = string.Equals(_skyTier, "black", StringComparison.OrdinalIgnoreCase);
        bool hd = string.Equals(_skyTier, "hd", StringComparison.OrdinalIgnoreCase);
        if (sky is not null)
        {
            // P862 — tell ONLY the tier that draws (each one owns its own planet
            // poster + dome caches; the others would decode the same art for
            // nothing). The latch keeps a tier change working.
            if (black)
            {
                if (_skySectorBlack != skySectorId) { _skySectorBlack = skySectorId; _blackSky.SetCluster(sky.Value); }
            }
            else if (hd)
            {
                if (_skySectorHd != skySectorId) { _skySectorHd = skySectorId; _hdSky.SetCluster(sky.Value); }
            }
            else
            {
                if (_skySectorNative != skySectorId) { _skySectorNative = skySectorId; _nativeSky.SetCluster(sky.Value); }
            }
        }

        if (sky is not null && black)
        {
            NativeScene.DrawSky(_blackSky, view, projection, cameraEye, _spaceVisualFrame);
            // (The aleph destination-glimpse composites after the opaque scene —
            // see DrawAlephGlimpses's call site in Draw — so the funnel doesn't
            // cover it.)
        }
        else if (sky is not null && !hd)
        {
            _nativeSky.Draw(view, projection, cameraEye, _spaceVisualFrame);
            // (The aleph destination-glimpse composites after the opaque scene —
            // see DrawAlephGlimpses's call site in Draw — so the funnel doesn't
            // cover it.)
        }
        else if (sky is not null)
        {
            // P577 — the HD sky tier (real telescope data). Same per-sector
            // structure and cluster switch as native; only the textures and the
            // star layer differ (see HdSkyRenderer). SetCluster happens in the
            // shared sector-change block above.
            _hdSky.Draw(view, projection, cameraEye, _spaceVisualFrame);
        }
        else
        {
            // No cluster yet — fall back to the old parallax field so there is
            // still a visible backdrop pre-flight.
            _starFar.Draw(view, projection, cameraEye, _spaceVisualFrame);
            _starMid.Draw(view, projection, cameraEye, _spaceVisualFrame);
            _starNear.Draw(view, projection, cameraEye, _spaceVisualFrame);
        }
    }

    /// <summary>
    /// P576-B — gather the current sector's warps that have a resolvable
    /// destination cluster and hand them to the glimpse renderer (which draws a
    /// faint destination-sky view into each funnel centre). Nearest-first +
    /// bounded by the renderer. No-op when the glimpse setting is off.
    /// </summary>
    private void DrawAlephGlimpses(Matrix view, Matrix projection, Vector3 cameraEye,
        ClientWorld world, DataClusterIGC current)
    {
        // P862b — resolve each warp's destination SECTOR through its partner warp
        // (native warpIGC.h:129-141, the P540c rule WarpDestinationResolver owns),
        // never by feeding the partner WARP id into the cluster table: the two id
        // spaces do not overlap (this map: clusters 1050-1062, warps 2150-2193), so
        // the old lookup never matched and the glimpse drew nothing at all.
        var targets = Rendering.AlephGlimpseRenderer.CollectTargets(
            world.Warps, world.Clusters, current.ClusterId, cameraEye);
        if (targets.Count == 0) return;
        LogGlimpseResolution(targets, current);
        // P585 — pass the per-warp transit-pulse strength (drives the glimpse
        // open-wide + lens-flare) and the flare occlusion test (P547 depth).
        _alephGlimpseRenderer.Visuals = _visuals;
        NativeScene.DrawGlimpses(_alephGlimpseRenderer, targets, cameraEye, view, projection, current,
            wId => _warpPulses.Strength(wId, _nowSeconds),
            TransitFlareOccluded);
    }

    /// <summary>P862b — name the glimpse's resolution once per warp: the aleph, the
    /// partner warp it points at and the SECTOR that partner sits in. This is the
    /// evidence that the glimpse aims at a real sector (and which one), which the
    /// [sector-entry] / [asset] lines cannot show.</summary>
    private void LogGlimpseResolution(List<Rendering.GlimpseTarget> targets, DataClusterIGC current)
    {
        foreach (var t in targets)
        {
            if (_glimpseLogged.TryGetValue(t.Warp.WarpId, out short known) && known == t.DestSector) continue;
            _glimpseLogged[t.Warp.WarpId] = t.DestSector;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[glimpse] warp #{t.Warp.WarpId} '{t.Warp.Name}' in {current.Name} ({current.ClusterId}) "
                + $"-> {t.Dest.Name} ({t.DestSector}) via partner warp #{t.Warp.DestinationId}"));
        }
    }

    /// <summary>
    /// P781 — emit the lead-up teleport sparks for this frame from the emitters
    /// the gate-locked pose pass gathered (<see cref="_teleportEmitters"/>).
    /// Native calls <c>AddTeleportParticles</c> once per rendered frame per ship
    /// (thinggeo.cpp:503-528), which scales the spray density with the frame
    /// rate; here a fixed <see cref="Rendering.WarpTransitParticles.EmissionClockSeconds"/>
    /// (60 Hz) accumulator gates the emission to at most one native call's worth
    /// of sparks per 1/60 s — a deliberate deviation (see WarpTransitParticles
    /// remarks and the P781 report). Runs on the render thread; the emitters are
    /// a plain snapshot (no gate needed).
    /// </summary>
    private void EmitTeleportLeadup(float frameSeconds)
    {
        NativeScene.EmitTeleportLeadup(_warpParticles, _teleportEmitters.AsSpan(0, _teleportEmitterCount),
            frameSeconds, ref _teleportEmitAccumulator, _trailRandom);
    }

    private void DrawSceneBuildings(ClientWorld world, short me, Vector3 cameraEye, Matrix view, Matrix proj)
    {
        // P997: native's two rotating build.mdl shells. Native lifetime
        // (default): the effect table itself, so the shell keeps shrinking
        // and fading around the new station until m_timeComplete
        // (Igc/buildingeffectigc.h:160-183, 310-358; appmsg.cpp:1692-1718)
        // and leaves on 181, an explosive rock death or reset. The
        // visuals.constructionHandoff flag instead draws ClientWorld's
        // presentation table, which retires the shell at the hand-off.
        bool instantHandoff = _visuals.IsOn(Rendering.VisualFlags.ConstructionHandoffKey);
        _drawBuilds.Clear();
        lock (_session.Gate)
        {
            long buildNow = ClientNowMilliseconds;
            world.PruneExpiredBuildingEffects(buildNow);
            if (TrySceneSector(world, me, out short myBeSector))
            {
                foreach (var (_, be) in instantHandoff ? world.ConstructionVisuals : world.BuildingEffects)
                {
                    if (be.ClusterId != myBeSector) continue;
                    var phase = be.Phase(buildNow);
                    _drawBuilds.Add(phase);
                }
            }
        }
        foreach (var phase in _drawBuilds)
        {
            if (OutsideRadialView(PoseConverter.ToXna(phase.Position), cameraEye, phase.Radius)) continue;
            NativeScene.DrawBuilding(_buildingEffectRenderer, phase, cameraEye, view, proj, (float)_nowSeconds);
        }
    }

    private void DrawSceneTeleport(Vector3 cameraEye, Matrix view, Matrix proj, float dt)
    {
        if (_pendingTeleportBurst is { } burst)
        {
            _pendingTeleportBurst = null;
            // P338 — the burst is the LOCAL ship transiting, so size the
            // spark spray to its real rendered radius (0.5 × HullType.Length)
            // instead of the fixed 50-unit stand-in; fall back when the
            // hull is unknown.
            float transitRadius = _myRenderRadius > 0f ? _myRenderRadius : Rendering.WarpTransitParticles.ShipRadiusDefault;
            NativeScene.EmitTransitBurst(_warpParticles, burst.Position, burst.Velocity, burst.Forward,
                burst.Right, transitRadius, _trailRandom);
        }

        // P781 — the lead-up spray (native AddTeleportParticles runs every
        // frame for every visible ripcording / aleph-closing ship,
        // thinggeo.cpp:619-696): emit from the emitters the gate-locked
        // pose pass gathered (_teleportEmitters), paced by the fixed 60 Hz
        // emission accumulator (native's per-rendered-frame clock would
        // scale the spray with fps — see WarpTransitParticles remarks).
        EmitTeleportLeadup((float)dt);
        _drawSparkViews.Clear();
        _warpParticles.CopyTo(_drawSparkViews);   // P781 — non-allocating gather (was AddRange(Enumerate()))
        // P781 — the benchmark reducer now runs AFTER the gather (it used
        // to trim the list before it was refilled, a no-op).
        ReduceBenchmarkEffects(_drawSparkViews);
        _warpParticleRenderer.Draw(_drawSparkViews, cameraEye, view, proj);
    }

    private void DrawSceneStrobes(Vector3 cameraEye, Matrix view, Matrix proj)
    {
        foreach (var kv in _navStrobeEmitters)
        {
            var ns = kv.Value;
            NativeScene.AppendShipStrobes(ns.Position, ns.Orientation, ns.Radius, true, (float)_nowSeconds, _drawNavStrobes);
        }
        _navStrobeRenderer.Draw(_drawNavStrobes, cameraEye, view, proj);
    }

}
#endif
