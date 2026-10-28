using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P862 — <b>sector load</b>: the per-sector asset warm-up that keeps a warp from
/// stalling the frame, plus the one-line accounting that proves what a sector
/// entry actually cost.
///
/// <para><b>The stall.</b> Every model's native mesh, native texture and HD
/// material is created lazily, inside the first Draw that needs it. Nothing about
/// a sector change creates work that a first sighting of the same model would not
/// — but a warp from an empty sector into one never drawn before (Alderan: 134
/// rocks in three rock models, its stations, the hulls of the ships already
/// fighting there, its planet) makes ALL of that land in the ONE frame that first
/// draws the new sector. The port's assets are HD: a native rock/planet texture is
/// a 2048² RGB565 <c>.mdl</c> (8.4 MB) that decodes into a 16.7 MB RGBA buffer and
/// uploads to a 16.7 MB GPU surface, so four or five of them in one frame is the
/// measured 18 fps hitch. Native Allegiance never pays this inside a frame: every
/// model loads its mesh + texture in its own constructor at mission load
/// (<c>Igc/asteroidigc.cpp:32</c>, <c>Igc/stationIGC.cpp:240</c>), hulls are
/// preloaded from the core (<c>Igc/hullTypeIGC.cpp:47</c> →
/// <c>WinTrek/trekigc.cpp:4912</c>), and the cluster change itself only swaps the
/// environment geo (<c>WinTrek/trekigc.cpp:2746</c>). The port had drifted to
/// lazy-on-first-draw; this restores the native "art is loaded up front" shape and
/// adds the per-frame budget a modern HD asset set needs.</para>
///
/// <para><b>The fix.</b> Resolve a sector's assets <em>before</em> the frame that
/// needs them, under a per-frame time budget:
/// <list type="bullet">
/// <item>the own sector as soon as the mission's static data is known (while the
/// pilot is still docked — those frames are not in flight);</item>
/// <item>the sector behind an aleph as soon as the own ship is on approach
/// (<see cref="AlephPromoteRadius"/>), promoted ahead of everything else, so a
/// warp — human or autopilot — finds every rock, station, hull and sky asset
/// already resident.</item>
/// </list>
/// Each warm item calls the SAME loader the draw path calls, so the resource is
/// created exactly once and stays resident for the match: a second entry into the
/// same sector does no new loads at all (<see cref="SectorWarmupQueue.IsWarmed"/>).
/// <c>ALLEGIANCE_SECTOR_PREWARM=0</c> turns the warm-up off (the A/B switch this
/// task's harness uses).</para>
///
/// <para><b>Native intent, port policy.</b> The native rule is "load the art up
/// front, never in the frame that first needs it" (citations above). P862 restores
/// that; the scheduling (approach distance, per-frame budget, priority) is the
/// port's own, because native loaded everything synchronously at mission start and
/// never had to think about a 16.7 ms frame. Nothing it warms changes how anything
/// looks: the renderers, caches, textures and draw calls are untouched.</para>
/// </summary>
public sealed partial class Game1
{
    /// <summary>Per-frame warm-up budget while flying — a 60 fps frame has ~16.7 ms
    /// and the scene owns most of it, so two milliseconds are enough to spread a
    /// sector's set over the seconds of an aleph approach.</summary>
    private const double WarmupFlightFrameMs = 2.0;

    /// <summary>Per-frame budget while docked / not flying: those frames carry no
    /// flight, so the whole set is warmed promptly.</summary>
    private const double WarmupIdleFrameMs = 12.0;

    /// <summary>Distance from an aleph at which its destination sector is warmed.
    /// Sized so the work finishes before the crossing at any hull speed the
    /// approach law produces (a fighter covers 7000 u in ~10 s).</summary>
    private const float AlephPromoteRadius = 7000f;

    /// <summary>Cap on distinct hull models warmed for one sector's ships — a
    /// showcase sector can hold hundreds of ships but only a handful of hull
    /// types. Bounds the GPU memory a single approach can add (see the report's
    /// cost table).</summary>
    private const int WarmupMaxSectorHulls = 24;

    /// <summary>Items taking at least this long log a per-item line (a 2048² texture
    /// decode + upload is hundreds of milliseconds). 0 disables the per-item line.</summary>
    private static readonly double WarmItemLogThresholdMs =
        double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_SECTOR_PREWARM_LOG"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold) ? threshold : 8.0;

    /// <summary>How often the approach scan walks the own sector's alephs (ms).
    /// The scan is a handful of distance tests; it does not need every frame.</summary>
    private const long WarmupApproachScanMs = 200;

    private SectorWarmupQueue? _warmQueue;
    private WarmupBudget? _warmBudget;
    private bool _warmConfigured;
    private bool _warmEnabled = true;
    private long _warmNextApproachScan;
    private readonly HashSet<short> _warmSectorsSeeded = new();
    private readonly Dictionary<short, WarmTally> _warmTallies = new();
    private short _warmApproachSector = -1;

    // ── sector-entry accounting (P862 diagnostics) ───────────────────────────
    private short? _sectorLoadLastSector;
    private int _sectorLoadWatchFrames;
    private LoadCounters _sectorLoadBefore;
    private LoadCounters _sectorLoadFrameBefore;
    private long _sectorLoadFrameAlloc;
    private long _sectorLoadLastFrame = -1;
    private long _sectorLoadSyntheticFrame;
    private long _sectorLoadAllocBefore;
    private int _sectorLoadGc0Before, _sectorLoadGc1Before, _sectorLoadGc2Before;
    private TimeSpan _sectorLoadPauseBefore;
    private long _sectorLoadChangeTicks;
    private short _sectorLoadTo;
    private int _sectorLoadShotAfterFrames;   // P862 proof shots still owed for this change
    private readonly string _sectorShotsDir = Environment.GetEnvironmentVariable("ALLEGIANCE_SECTOR_SHOTS") ?? "";
    private bool _sectorShotPreArmed;

    /// <summary>The per-sector warm tally (diagnostics).</summary>
    private sealed class WarmTally
    {
        public int Items;
        public int Created;
        public long StartTicks;
        public double Ms;
        public bool Reported;
    }

    /// <summary>What is currently resident in the per-type asset caches. Sampled
    /// once per frame (ten integer reads) so a sector change can report exactly
    /// what the entry frames had to create.</summary>
    private readonly record struct LoadCounters(
        int RockModels, int RockTextures,
        int StationModels, int StationTextures,
        int ShipModels, int ShipTextures,
        int Planets, int HdMaterials)
    {
        public static LoadCounters operator -(LoadCounters a, LoadCounters b) => new(
            a.RockModels - b.RockModels, a.RockTextures - b.RockTextures,
            a.StationModels - b.StationModels, a.StationTextures - b.StationTextures,
            a.ShipModels - b.ShipModels, a.ShipTextures - b.ShipTextures,
            a.Planets - b.Planets, a.HdMaterials - b.HdMaterials);

        /// <summary>Every count that can only come from a decode/upload.</summary>
        public int Total => Math.Max(0, RockModels) + Math.Max(0, RockTextures)
                          + Math.Max(0, StationModels) + Math.Max(0, StationTextures)
                          + Math.Max(0, ShipModels) + Math.Max(0, ShipTextures)
                          + Math.Max(0, Planets) + Math.Max(0, HdMaterials);

        public override string ToString()
            => $"rock {RockModels}M/{RockTextures}T, station {StationModels}M/{StationTextures}T, "
             + $"ship {ShipModels}M/{ShipTextures}T, planet +{Planets}, HD +{HdMaterials}";
    }

    private LoadCounters SampleLoadCounters()
    {
        if (_nativeAsteroidRenderer is null || _nativeStationRenderer is null || _nativeShipRenderer is null)
            return default;
        // The planet poster is owned by the ACTIVE sky tier (see DrawSkyP576).
        int planets = string.Equals(_skyTier, "hd", StringComparison.OrdinalIgnoreCase) ? _hdSky.LoadedPlanetTextures
            : string.Equals(_skyTier, "black", StringComparison.OrdinalIgnoreCase) ? _blackSky.LoadedPlanetTextures
            : _nativeSky.LoadedPlanetTextures;
        return new LoadCounters(
            _nativeAsteroidRenderer.LoadedModelCount, _nativeAsteroidRenderer.LoadedTextureCount,
            _nativeStationRenderer.LoadedModelCount, _nativeStationRenderer.LoadedTextureCount,
            _nativeShipRenderer.LoadedModelCount, _nativeShipRenderer.LoadedTextureCount,
            planets, _hdMaterials?.ResidentMaterials ?? 0);
    }

    private void EnsureWarmupConfigured()
    {
        if (_warmConfigured) return;
        _warmConfigured = true;
        // P862 — the A/B switch the harness uses: a bare "0"/"off"/"false"
        // disables the pre-warm and restores the pre-P862 lazy-on-first-draw path.
        var raw = Environment.GetEnvironmentVariable("ALLEGIANCE_SECTOR_PREWARM");
        _warmEnabled = !(string.Equals(raw, "0", StringComparison.Ordinal)
                      || string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase));
        if (!_warmEnabled)
        {
            Console.WriteLine("[sector-warm] DISABLED by ALLEGIANCE_SECTOR_PREWARM — assets load lazily on first draw (P862 A/B baseline)");
            return;
        }
        _warmQueue = new SectorWarmupQueue();
        _warmBudget = new WarmupBudget(WarmupFlightFrameMs);
        Console.WriteLine($"[sector-warm] enabled: own-sector at mission data, aleph approach within {AlephPromoteRadius:0} u, "
            + $"{WarmupFlightFrameMs:0.#} ms/frame flying ({WarmupIdleFrameMs:0.#} ms docked)");
    }

    /// <summary>
    /// P862 — one frame of sector load work: detect a sector change (accounting),
    /// seed the own sector / an approached sector, then drain the queue under the
    /// frame budget. Called once per flying frame, right after the render poses are
    /// gathered, so it runs on the render thread next to every other world read.
    /// </summary>
    private void UpdateSectorLoad()
    {
        EnsureWarmupConfigured();
        UpdateSectorEntryAccounting();
        if (!_warmEnabled || _warmQueue is null || _warmBudget is null) return;

        var world = _session.World;
        short ownSector = -1;
        Vector3 ownPos = Vector3.Zero;
        bool haveOwn = false;
        bool docked = true;
        lock (_session.Gate)
        {
            if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var mine))
            {
                ownSector = RenderSector(mine.SectorId);
                docked = mine.StationId != -1;
            }
            if (ownSector < 0 && world.Clusters.Count > 0)
            {
                // Not seated yet: the home sector's assets are what the first
                // flying frame will need, so warm those while the client waits.
                foreach (var (_, st) in world.Stations)
                    if (st.SideId == (world.MySideId ?? 0)) { ownSector = st.ClusterId; break; }
            }
        }
        if (TryOwnPose(out var posePos, out _, out _))
        {
            ownPos = PoseConverter.ToXna(posePos);
            haveOwn = true;
        }

        bool ready = world.WorldReady;

        // 1) The own sector, as soon as its definitions are known. The type
        //    catalogue is required for station models, so a sector is only marked
        //    seeded once it exists (otherwise the station items would be lost for
        //    the whole match).
        if (ready && ownSector >= 0 && _shopCatalog is not null && _warmSectorsSeeded.Add(ownSector))
            SeedSectorWarmup(ownSector, includeShips: true);

        // 2) An aleph the own ship is approaching: its destination sector becomes
        //    the most likely next scene, so warm it and put it first in the queue.
        if (ready && haveOwn && Environment.TickCount64 >= _warmNextApproachScan)
        {
            _warmNextApproachScan = Environment.TickCount64 + WarmupApproachScanMs;
            ScanAlephApproach(world, ownSector, ownPos);
        }

        // 3) Drain under the budget.
        _warmBudget.FrameBudgetMs = docked ? WarmupIdleFrameMs : WarmupFlightFrameMs;
        _warmBudget.BeginFrame();
        while (_warmQueue.PendingCount > 0 && _warmBudget.CanStart())
        {
            if (!_warmQueue.TryTake(out var asset)) break;
            var tally = TallyFor(asset.SectorId);   // before the item, so `elapsed` includes it
            long started = Stopwatch.GetTimestamp();
            int outcome = WarmAssetItem(asset);
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _warmBudget.Spend(ms);
            _warmQueue.Complete(asset, created: outcome > 0, failed: outcome < 0);
            tally.Items++;
            if (outcome > 0) tally.Created++;
            // One line per heavy item is the evidence a report/harness quotes
            // ("this rock texture cost 240 ms"), and it is what makes a slow
            // asset visible without a profiler. Cheap items stay silent.
            if (WarmItemLogThresholdMs > 0 && ms >= WarmItemLogThresholdMs)
            {
                string hint = string.IsNullOrEmpty(asset.Hint) ? "" : $" ('{asset.Hint}')";
                string result = outcome > 0 ? "new" : outcome < 0 ? "unavailable" : "already resident";
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[sector-warm]   {asset.Kind.ToString().ToLowerInvariant()} {asset.Stem}{hint}: {ms:F1} ms ({result})"));
            }
            tally.Ms += ms;
            if (_warmQueue.PendingFor(asset.SectorId) == 0) ReportSectorWarmComplete(asset.SectorId, tally);
        }
    }

    private WarmTally TallyFor(short sectorId)
    {
        if (_warmTallies.TryGetValue(sectorId, out var tally)) return tally;
        tally = new WarmTally { StartTicks = Stopwatch.GetTimestamp() };
        _warmTallies[sectorId] = tally;
        return tally;
    }

    private void ReportSectorWarmComplete(short sectorId, WarmTally tally)
    {
        if (tally.Reported || tally.Items == 0) return;
        tally.Reported = true;
        var name = SectorName(sectorId);
        double elapsed = Stopwatch.GetElapsedTime(tally.StartTicks).TotalSeconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[sector-warm] {name} ({sectorId}): {tally.Items} item(s) pre-warmed, "
            + $"{tally.Created} new, {tally.Ms:F1} ms of loader time over {elapsed:F1} s "
            + $"({_warmQueue!.AlreadyResidentCount} already resident so far, {_warmQueue.FailedCount} unavailable)"));
    }

    private string SectorName(short sectorId)
    {
        lock (_session.Gate)
            return _session.World.Clusters.TryGetValue(sectorId, out var c) ? c.Name : "?";
    }

    /// <summary>Seed every per-type asset a sector needs: its rocks, its station
    /// types, its sky and — when the client already knows which hulls are out
    /// there — those hulls. All of it is idempotent (the queue dedupes across
    /// sectors, so two sectors sharing a rock model load it once).</summary>
    private int SeedSectorWarmup(short sectorId, bool includeShips)
    {
        if (_warmQueue is null) return 0;
        var world = _session.World;
        int added = 0;
        lock (_session.Gate)
        {
            var rocks = new List<(string?, string?)>();
            foreach (var (_, ast) in world.Asteroids)
                if (ast.ClusterId == sectorId) rocks.Add((ast.ModelName, ast.TextureName));
            added += _warmQueue.EnqueueAll(SectorWarmupPlanner.Rocks(sectorId, rocks));

            // Stations resolve their model through the type catalogue the shop
            // already holds (the same lookup ResolveStationModel does).
            if (_shopCatalog?.StationTypes is { } types)
            {
                var stations = new List<(string?, string?)>();
                foreach (var (_, st) in world.Stations)
                    if (st.ClusterId == sectorId && types.TryGetValue(st.StationTypeId, out var type))
                        stations.Add((type.ModelName, type.TextureName));
                added += _warmQueue.EnqueueAll(SectorWarmupPlanner.Stations(sectorId, stations));
            }

            if (world.Clusters.TryGetValue(sectorId, out var cluster))
            {
                var sky = SectorWarmupPlanner.Sky(sectorId, cluster.PlanetName, cluster.PosterName);
                if (SectorWarmupPlanner.HasSky(sky) && _warmQueue.Enqueue(sky)) added++;
            }

            if (includeShips)
            {
                var hulls = new List<(string?, string?)>();
                var seen = new HashSet<short>();
                foreach (var (_, ship) in world.Ships)
                {
                    if (ship.SectorId != sectorId) continue;
                    if (!seen.Add(ship.HullId)) continue;
                    if (seen.Count > WarmupMaxSectorHulls) break;
                    if (_hullById.TryGetValue(ship.HullId, out var hull))
                        hulls.Add((hull.ModelName, hull.TextureName));
                }
                added += _warmQueue.EnqueueAll(SectorWarmupPlanner.Ships(sectorId, hulls));
            }
        }
        return added;
    }

    /// <summary>Warm the sector behind an aleph the own ship is closing on. Native
    /// gives no warning before a transit (the server moves the ship the frame it
    /// touches the aleph), so the approach itself is the only lead time — the same
    /// seconds the aleph destination glimpse already spends rendering that
    /// sector's sky.</summary>
    private void ScanAlephApproach(Allegiance.ClientCore.State.ClientWorld world, short ownSector, Vector3 ownPos)
    {
        if (_warmQueue is null || ownSector < 0) return;
        short approach = -1;
        float bestSq = float.MaxValue;
        lock (_session.Gate)
        {
            foreach (var (_, warp) in world.Warps)
            {
                if (warp.ClusterId != ownSector || warp.Position is not { Length: 3 }) continue;
                float dx = warp.Position[0] - ownPos.X, dy = warp.Position[1] - ownPos.Y, dz = warp.Position[2] - ownPos.Z;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 > AlephPromoteRadius * AlephPromoteRadius || d2 >= bestSq) continue;
                if (Allegiance.Core.Model.WarpDestinationResolver.DestinationSector(world.Warps, warp.WarpId) is not { } dest) continue;
                if (dest == ownSector) continue;
                bestSq = d2;
                approach = dest;
            }
        }
        if (approach < 0)
        {
            _warmApproachSector = -1;
            _sectorShotPreArmed = false;
            return;
        }

        // Re-seed on every scan, not just the first: ships keep arriving in the
        // destination sector while the own ship closes on the aleph, so a hull
        // texture first seen a second before the transit must still be warmed
        // (measured: one late hull still cost 982 ms inside the arrival frame).
        // Seeding is idempotent — the queue dedupes every (kind, stem).
        // Optionally capture the departure side: one frame with the aleph filling
        // the view, ~1.5 km out, just before the crossing (ALLEGIANCE_SECTOR_SHOTS).
        if (_sectorShotsDir.Length > 0 && bestSq <= 1800f * 1800f && !_sectorShotPreArmed)
        {
            _sectorShotPreArmed = true;
            ScheduleSectorShot($"sector_{ownSector}_pre1");
        }

        bool firstScan = approach != _warmApproachSector;
        int added = SeedSectorWarmup(approach, includeShips: true);
        int promoted = _warmQueue.Promote(approach);
        if (!firstScan && added == 0) return;          // nothing new: stay quiet
        _warmApproachSector = approach;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[sector-warm] aleph approach -> {SectorName(approach)} ({approach}): {added} asset(s) queued, "
            + $"{promoted} promoted to the front ({MathF.Sqrt(bestSq):0} u to the aleph)"));
    }

    /// <summary>Warm ONE item by calling the renderer that owns it. Returns 1 when
    /// a new resource was created, 0 when it was already resident, -1 when the art
    /// is unavailable (the renderer has already logged it once).</summary>
    private int WarmAssetItem(in WarmAsset asset)
    {
        switch (asset.Kind)
        {
            case WarmAssetKind.Rock:
            {
                if (!_asteroidsNative) return 0;
                int created = _nativeAsteroidRenderer.Warm(asset.Stem, asset.Hint);
                if (created < 0) return created;
                string? tex = _nativeAsteroidRenderer.TextureStemFor(asset.Stem, asset.Hint);
                if (_hdMaterials is not null && tex is not null
                    && _hdMaterials.Warm(asset.Stem, tex) > 0) created = 1;
                return created;
            }
            case WarmAssetKind.Station:
            {
                // Only the native station path caches per type; the procedural
                // placeholder is a single shared mesh (loaded at startup).
                int created = _nativeStationRenderer.Warm(asset.Stem, asset.Hint);
                if (created < 0) return created;
                string? tex = _nativeStationRenderer.TextureStemFor(asset.Stem, asset.Hint);
                if (_hdMaterials is not null && tex is not null
                    && _hdMaterials.Warm(asset.Stem, tex) > 0) created = 1;
                return created;
            }
            case WarmAssetKind.Ship:
            {
                if (!_shipsNative) return 0;
                int created = _nativeShipRenderer.Warm(asset.Stem, asset.Hint);
                if (created < 0) return created;
                string? tex = _nativeShipRenderer.TextureStemFor(asset.Stem, asset.Hint);
                if (_hdMaterials is not null && tex is not null
                    && _hdMaterials.Warm(asset.Stem, tex) > 0) created = 1;
                return created;
            }
            case WarmAssetKind.Sky:
            {
                int created = 0;
                // Planet poster: each tier owns one PlanetPoster, so warm the
                // ACTIVE tier's instance (the one that will draw it).
                if (!string.IsNullOrEmpty(asset.Stem))
                {
                    int planet = string.Equals(_skyTier, "hd", StringComparison.OrdinalIgnoreCase) ? _hdSky.WarmPlanet(asset.Stem)
                        : string.Equals(_skyTier, "black", StringComparison.OrdinalIgnoreCase) ? _blackSky.WarmPlanet(asset.Stem)
                        : _nativeSky.WarmPlanet(asset.Stem);
                    if (planet > 0) created = 1;
                    else if (planet < 0 && asset.Hint is null) return -1;
                }
                // The tier's per-poster dome/environment art (native nebula or the
                // HD nebula PNG). The black tier draws no dome, so it warms none.
                if (!string.IsNullOrEmpty(asset.Hint))
                {
                    int dome = string.Equals(_skyTier, "hd", StringComparison.OrdinalIgnoreCase) ? _hdSky.Warm(asset.Hint)
                        : string.Equals(_skyTier, "native", StringComparison.OrdinalIgnoreCase) ? _nativeSky.Warm(asset.Hint)
                        : 0;
                    if (dome > 0) created = 1;
                }
                return created;
            }
            default:
                return 0;
        }
    }

    /// <summary>
    /// P862 — the sector-entry accounting: on the frame the own ship's sector
    /// changes, note the frame's own wall time and start a short watch window, then
    /// report exactly what the entry frames created (native meshes/textures, planet
    /// textures, HD material uploads) plus the managed allocation and GC cost. This
    /// is the measurement the report and a live run quote; it is three integer
    /// reads per frame and one line per transit.
    /// </summary>
    private void UpdateSectorEntryAccounting()
    {
        var world = _session.World;
        short? sector = null;
        lock (_session.Gate)
            if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var mine))
                sector = RenderSector(mine.SectorId);
        // 0/negative is "no sector yet" (unseated, docking, or a transient state) —
        // not a transit. Without this a dock/undock edge read as a sector change and
        // spammed the accounting and the proof shots (observed).
        if (sector is <= 0) sector = null;

        // Updates can run several times between two Draws (a fixed-timestep catch-up
        // after a stall, or an uncapped loop), so the accounting is keyed on the
        // rendered FRAME id — the window must measure frames, not update calls.
        long frame = _frameTelemetry?.FrameId ?? ++_sectorLoadSyntheticFrame;
        if (frame == _sectorLoadLastFrame) return;
        _sectorLoadLastFrame = frame;

        long now = Stopwatch.GetTimestamp();
        if (_sectorLoadLastSector is { } previous && sector is { } current && current != previous
            && previous > 0 && current > 0)
        {
            // The frame that saw the change: its full cost is the wall time from
            // the previous Update to this one (Draw + present included).
            double frameMs = _sectorLoadChangeTicks > 0
                ? Stopwatch.GetElapsedTime(_sectorLoadChangeTicks, now).TotalMilliseconds : 0;
            _sectorLoadTo = current;
            _sectorLoadWatchFrames = 30;
            _sectorLoadShotAfterFrames = 2;   // the first frame in the sector, then +30
            _sectorLoadBefore = SampleLoadCounters();
            _sectorLoadFrameBefore = _sectorLoadBefore;
            _sectorLoadFrameAlloc = GC.GetTotalAllocatedBytes(false);
            _sectorLoadAllocBefore = GC.GetTotalAllocatedBytes(false);
            _sectorLoadGc0Before = GC.CollectionCount(0);
            _sectorLoadGc1Before = GC.CollectionCount(1);
            _sectorLoadGc2Before = GC.CollectionCount(2);
            _sectorLoadPauseBefore = GC.GetTotalPauseDuration();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[sector-entry] {SectorName(previous)} ({previous}) -> {SectorName(current)} ({current}) "
                + $"frame {frame.ToString(CultureInfo.InvariantCulture)}: "
                + $"entry frame {frameMs:F1} ms; warming {(_warmEnabled ? "on" : "OFF (A/B baseline)")}; "
                + $"client knows {KnownSectorObjects(current)} here; resident before entry: {_sectorLoadBefore}"));
            _sectorLoadChangeTicks = now;
        }
        _sectorLoadLastSector = sector;

        // Per-frame deltas for the first frames in the new sector: that is where a
        // lazily-loaded asset lands, and naming the frame is what identifies WHICH
        // subsystem paid (a rock texture, a glimpse's destination planet, …).
        if (_sectorLoadWatchFrames > 0 && _sectorLoadWatchFrames >= 24)
        {
            var nowCounters = SampleLoadCounters();
            var step = nowCounters - _sectorLoadFrameBefore;
            long allocNow = GC.GetTotalAllocatedBytes(false);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[sector-entry]   frame {frame.ToString(CultureInfo.InvariantCulture)} "
                + $"(+{30 - _sectorLoadWatchFrames} after the change): loads {step}, "
                + $"alloc {(allocNow - _sectorLoadFrameAlloc) / 1024.0:F0} KB"));
            _sectorLoadFrameBefore = nowCounters;
            _sectorLoadFrameAlloc = allocNow;
        }

        // Post frames: the first frame in the new sector, then the settled field.
        // Guarded by a pending countdown so an idle watch (no change yet) never
        // queues a shot (observed: spurious `sector_0_after30` files).
        if (_sectorLoadShotAfterFrames > 0 && _sectorLoadWatchFrames is 30 or 0)
        {
            int after = 30 - _sectorLoadWatchFrames;
            _sectorLoadShotAfterFrames--;
            ScheduleSectorShot($"sector_{_sectorLoadTo}_after{after}");
        }

        if (_sectorLoadWatchFrames > 0 && --_sectorLoadWatchFrames == 0)
        {
            var after = SampleLoadCounters();
            var delta = after - _sectorLoadBefore;
            long alloc = GC.GetTotalAllocatedBytes(false) - _sectorLoadAllocBefore;
            int gc0 = GC.CollectionCount(0) - _sectorLoadGc0Before;
            int gc1 = GC.CollectionCount(1) - _sectorLoadGc1Before;
            int gc2 = GC.CollectionCount(2) - _sectorLoadGc2Before;
            double pause = (GC.GetTotalPauseDuration() - _sectorLoadPauseBefore).TotalMilliseconds;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[sector-entry] {SectorName(_sectorLoadTo)} ({_sectorLoadTo}) first 30 frames: "
                + $"{delta} ({_hdMaterials?.ResidentBytes / 1048576.0:F1} MiB HD resident) | "
                + $"allocated {alloc / 1048576.0:F1} MB, GC {gc0}/{gc1}/{gc2}, pause {pause:F1} ms"));
        }
        _sectorLoadChangeTicks = now;
    }

    /// <summary>What the client's own tables hold for a sector at entry: rocks and
    /// ships. A rock the client has never been told about can only be discovered
    /// from inside the sector, so a pre-warm cannot cover it — that is the number
    /// that decides whether a lazy load at entry is avoidable at all.</summary>
    private string KnownSectorObjects(short sectorId)
    {
        var world = _session.World;
        int rocks = 0, ships = 0;
        lock (_session.Gate)
        {
            foreach (var (_, ast) in world.Asteroids) if (ast.ClusterId == sectorId) rocks++;
            foreach (var (_, ship) in world.Ships) if (ship.SectorId == sectorId) ships++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{rocks} rock(s), {ships} ship(s)");
    }

    /// <summary>Schedule a labelled real-backbuffer shot at the end of this frame
    /// (<see cref="FlushScheduledAutoshot"/>) when the operator asked for sector
    /// proof frames (ALLEGIANCE_SECTOR_SHOTS=&lt;dir&gt;). The shots bracket the
    /// transit: one on the departure side at the aleph, then the first frame in the
    /// new sector and the settled field half a second later.</summary>
    private void ScheduleSectorShot(string name)
    {
        if (_sectorShotsDir.Length == 0) return;
        _autoshotDir = _sectorShotsDir;
        _autoShotName = name;
        _autoShotScheduled = true;
        Console.WriteLine($"[sector-entry] shot queued: {name}");
    }
}
