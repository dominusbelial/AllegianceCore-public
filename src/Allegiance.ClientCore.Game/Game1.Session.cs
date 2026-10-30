using System.Net.Http;
using System.Net.Http.Json;
using System.Linq;
using System.Text;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Chat;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.QuickChat;
using Allegiance.ClientCore.MonoGame.Capture;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Audio;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;
using NumericsVec2 = System.Numerics.Vector2;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// The session slice of <see cref="Game1"/> (P384 stage-5 split from <c>Game1.cs</c>).
/// Partial class — shares the <c>Game1</c> type, so private field/method access
/// across the split files is unaffected.
/// </summary>
public sealed partial class Game1
{


    /// <summary>Everything needed to boot <see cref="Game1"/> — which mode it
    /// starts in (lobby vs direct connect) and the config each mode needs.
    /// Built by <c>Program.cs</c> from the CLI args.</summary>
    public sealed class LaunchOptions
    {
        public Allegiance.SinglePlayer.Benchmark.BenchmarkCommand? Benchmark;
        public string PlayerName = "monogame";
        public string? BenchmarkCollectorUrl;

        /// <summary>Non-null selects DIRECT connect (skip the lobby), connecting
        /// straight to <see cref="DirectPort"/> on loopback — the unchanged
        /// <c>[host] [port] [name]</c> dev/testing shortcut.</summary>
        public string? DirectHost;

        public ushort DirectPort;

        /// <summary>Base URL of the lobby directory whose <c>/api/servers</c>
        /// the server-browser state polls. Default documented in
        /// <see cref="ServerListing.LobbyUrlDefault"/>.</summary>
        public string LobbyUrl = ServerListing.LobbyUrlDefault;

        /// <summary>
        /// P731 — when true (no game-starting positional args, no capture, no
        /// inspect), boot into the intro screen ("choose single player or
        /// multiplayer at start", native's introscreen.cpp) instead of the
        /// lobby. Command-line args (<c>[host] [port] [name]</c>, capture,
        /// inspect) still bypass the intro entirely.
        /// </summary>
        public bool Intro = false;

        /// <summary>P306 — when non-null, this launch is a HEADLESS video
        /// capture: connect to the given host:port, render the whole fleet
        /// from an automatic overview camera into an offscreen
        /// <see cref="RenderTarget2D"/>, save a real PNG frame sequence
        /// (P207 offscreen-render recipe), log real render FPS, and exit
        /// after <see cref="CaptureSpec.DurationSeconds"/>. Skips the
        /// side-select / ready-check screens automatically.</summary>
        public CaptureSpec? Capture;

        /// <summary>P385 — client-only <c>--debugMode</c> launch flag (no wire
        /// exposure; paired by hand with the server's
        /// <c>liveSettings.debugMode</c> while testing). It drops the capture-
        /// overview camera to native's real default vertical FOV (100° → 50°,
        /// s_fDefaultFOV = 50°), so live framing can be compared against native
        /// without a permanent change. The in-flight chase/cockpit/missile cameras
        /// are <b>not</b> affected: P449b made those default to 50° in BOTH modes
        /// (see <see cref="FlightFovDegrees"/>), so --debugMode's only FOV effect
        /// left is the capture overview.</summary>
        public bool DebugMode;

        /// <summary>Initial window / back-buffer size in pixels (<c>--window WxH</c>).
        /// Null keeps the 1280×720 default. Client-only launch setting for clean,
        /// fixed-size recordings (e.g. 2560×1440 for YouTube); never sent on the wire.</summary>
        public int? WindowWidth;
        public int? WindowHeight;
        /// <summary><c>--borderless</c>: no title bar/frame, window at the screen's top-left, so a
        /// recording can use the full monitor height (e.g. 3840×2160 on a 2160-px-tall screen).</summary>
        public bool Borderless;

        /// <summary>P448b — render REMOTE ships through the P448a dead-reckoner
        /// (replay the real flight model forward from each update to "now") instead
        /// of their raw last-known pose. Default <b>ON</b>; the client-only
        /// <c>--noDeadReckon</c> launch flag (paired by hand with the server's
        /// liveSettings philosophy while testing, mirroring P385's --debugMode) turns
        /// it OFF and returns remote ships to their raw poses, for a cheap A/B
        /// comparison without touching the wire. Never sent on the wire. The local
        /// player's own ship is always rendered on its raw pose regardless (that is
        /// P447, out of scope).</summary>
        public bool RemoteShipDeadReckoning = true;

        /// <summary>P813b — EXPERIMENTAL (default OFF): render REMOTE ships through
        /// the cubic-Hermite interpolator on one render clock (RemoteShipHermiteInterpolator),
        /// drawing each ship slightly behind real time at an adaptive delay sized to
        /// its measured update cadence, instead of the P448b forward extrapolation.
        /// Client-only <c>--remoteHermite</c> launch flag; when OFF (default) the
        /// P448b dead-reckoner renders exactly as today. Never sent on the wire.
        /// The local player's own ship is never affected (own prediction is P447).</summary>
        public bool RemoteShipHermite = false;

        /// <summary>P508 — render the own ship from its local simulation pose (the
        /// client flies its own ship; default <b>ON</b>) vs the raw last
        /// <c>ServerShipUpdate</c> pose. The client-only <c>--noPrediction</c> launch
        /// flag (mirroring P385's --debugMode / P448b's --noDeadReckon) turns the
        /// <em>view</em> OFF to the raw server pose for a cheap A/B; the local
        /// simulation still runs and always drives the native SHIP_UPDATE (it is the
        /// authority), so this is purely a visualization toggle. Never sent on the wire.</summary>
        public bool OwnShipPrediction = true;

        /// <summary>P449a — the ambient space-dust density (native's user <c>Debris</c>
        /// preference, <c>m_userDistancePerParticle</c>, default 1.0), mirroring the
        /// server's <c>liveSettings</c> philosophy as a client-only launch setting
        /// (<c>--debris &lt;float&gt;</c>; never sent on the wire). 0 disables the
        /// field entirely (native treats <c>distancePerParticle == 0</c> as off,
        /// debrisgeo.cpp:98-106). Lower densities spawn grains less frequently.</summary>
        public float? DebrisDensity;   // --debris launch override; null = saved Debris preference

        /// <summary>P449b — the in-flight (chase/cockpit/missile) camera vertical FOV
        /// in degrees. Defaults to native's real default <c>s_fDefaultFOV = 50°</c>
        /// (<c>WinTrek.h:26</c>) in BOTH normal and debug mode — Domingo's decision:
        /// match native. The client-only <c>--flightFov &lt;deg&gt;</c> launch setting
        /// (mirroring the server's <c>liveSettings</c> philosophy by hand, like P385's
        /// --debugMode / P449a's --debris; never sent on the wire) overrides it. The
        /// separate capture-overview FOV stays governed by <c>--debugMode</c>.</summary>
        public float FlightFovDegrees = 50f;

        /// <summary>P546 — the VFX renderer: <b>Enhanced</b> (default ON) uses the
        /// CC0 flipbook explosions + animated premultiplied sector-lit smoke
        /// (<see cref="Rendering.EnhancedExplosionRenderer"/> /
        /// <see cref="Rendering.EnhancedSmokeField"/>); <b>Native</b> (OFF) keeps the
        /// exact P545/P263 native-art path (<see cref="Rendering.ExplosionRenderer"/> +
        /// native additive smoke) for parity checks. Client-only launch setting
        /// (<c>--fx native|enhanced</c>; never sent on the wire).</summary>
        public bool EnhancedVfx = true;

        /// <summary>P573 — the requested effects TIER (<c>--fx native|enhanced|volumetric|displaced</c>).
        /// Null = platform default (DesktopGL→Volumetric, Android→Enhanced). The
        /// effective tier is resolved by
        /// <see cref="State.EffectsQualityResolver.Resolve"/> (shader availability +
        /// GPU probe can drop Volumetric to Enhanced). Never sent on the wire.</summary>
        public State.EffectsQuality? EffectsRequested = null;
        public State.EffectsDetail? EffectsDetail = null;
        public bool? BloomOverride = null;

        /// <summary>P573 — which volumetric renderer the Volumetric tier uses:
        /// <see cref="State.VolumetricTechnique.RaymarchA"/> (raymarched volume,
        /// default) or <see cref="State.VolumetricTechnique.DisplacedSphereB"/>
        /// (<c>--fx displaced</c>). Ignored outside the Volumetric tier.</summary>
        public State.VolumetricTechnique VolumetricTech = State.VolumetricTechnique.RaymarchA;

        /// <summary>P547 — soft particles: the proper depth-fade for the Enhanced
        /// smoke (each puff fades where it meets the opaque scene's linear depth
        /// instead of slicing hard edges against hulls/rocks/stations). Default
        /// <b>ON</b> in Enhanced; meaningless in Native (which never constructs
        /// the enhanced smoke). Falls back to the near-fade-only path if the
        /// depth shader/target is unavailable. Client-only launch setting
        /// (<c>--soft off|on</c>; never sent on the wire).</summary>
        public bool SoftParticles = true;

        /// <summary>P547 — thresholded bloom (bright-pass + separable Gaussian,
        /// composited additively over the scene; HUD/UI drawn after so they
        /// don't bloom). Default <b>OFF</b>: bloom is a look toggle, and the
        /// un-thresholded spike version re-raised dense-combat clipping to
        /// ~90%; the thresholded shader here keeps dense combat ≤ ~20%. Only
        /// meaningful in Enhanced. Client-only launch setting (<c>--bloom
        /// on|off</c>; never sent on the wire).</summary>
        public bool Bloom = false;

        /// <summary>P566 — render ships from their ORIGINAL native <c>.mdl</c> meshes +
        /// native textures (default <b>ON</b>), versus the P287 Meshy AI content models.
        /// Client-only launch setting (<c>--ships native|meshy</c>; never sent on the
        /// wire). In <c>native</c> the Meshy path stays intact behind the fallback chain:
        /// a hull whose native art fails to decode falls back to Meshy, then to the
        /// placeholder. The docked/hangar loadout preview honours the same setting.</summary>
        public bool ShipsNative = true;

        /// <summary>P567 — render each asteroid from its OWN ORIGINAL native
        /// <c>.mdl</c> rock mesh + native texture (default <b>ON</b>), versus the
        /// P286 shared imported/procedural variants. Client-only launch setting
        /// (<c>--asteroids native|imported</c>; never sent on the wire). In
        /// <c>native</c> the imported path stays intact behind the fallback chain: a
        /// rock whose native model fails to decode falls back to the imported variant,
        /// then to the procedural rock.</summary>
        public bool AsteroidsNative = true;

        /// <summary>P576 — the sky tier (<c>--sky black|native|hd</c>).
        /// <b>Black</b> (default, P588) renders a pure black sky with the
        /// pre-P576 point starfield + the native planet poster (no nebula
        /// dome). <b>Native</b> (P576-A) adds the per-sector environment dome
        /// (seeded stars + dome + planet poster). <b>HD</b> (P577) swaps in
        /// real telescope data (NASA star map + Hubble nebula domes + native
        /// planets). Client-only launch setting; never sent on the wire.</summary>
        public string SkyTier = "black";

        /// <summary>P588 — resolve a requested sky-tier string to the canonical
        /// tier name: <c>black</c> (default) | <c>native</c> | <c>hd</c>.
        /// Unknown/empty values fall back to the default (black). Pure and
        /// unit-tested (the default must resolve to black).</summary>
        public static string EffectiveSkyTier(string? requested)
            => string.Equals(requested, "hd", StringComparison.OrdinalIgnoreCase) ? "hd"
             : string.Equals(requested, "native", StringComparison.OrdinalIgnoreCase) ? "native"
             : "black";

        /// <summary>P588 — whether a canonical sky tier draws an environment
        /// (nebula) dome. Only <c>black</c> does not — it draws point stars +
        /// the native planet poster and no dome (Domingo disliked the dome's
        /// "stitched fabric" seams). Pure and unit-tested.</summary>
        public static bool SkyTierDrawsDome(string tier)
            => !string.Equals(tier, "black", StringComparison.OrdinalIgnoreCase);

        /// <summary>P576/P607 — the aleph destination-glimpse + transit flare
        /// (<c>--aleph-glimpse</c>): faintly show the destination sector's sky
        /// inside each visible aleph's funnel centre and fire a flare +
        /// funnel-boost when any visible ship transits it. Default <b>ON</b>
        /// since P607 (Domingo wants the enemy-crossing flare visible out of
        /// the box); <c>--aleph-glimpse off</c> disables only the
        /// glimpse/flare extras — the native arrival-star swell for visible
        /// transits stays on either way. Client-only launch setting; never
        /// sent on the wire.</summary>
        public bool AlephGlimpse = true;

        /// <summary>P577 — global HD-sky intensity (<c>--sky-intensity F</c>,
        /// default 1.0, clamped to [0,2]): multiplies the whole HD sky
        /// backdrop, the readable-brightness lever of the P577 readability
        /// guard. Client-only; never sent on the wire.</summary>
        public float SkyIntensity = 1.0f;

        /// <summary>P577 — use the Android/low-res HD sky asset set
        /// (<c>--sky-lowres</c>): nebulae 1024x512, planets 512, star faces
        /// 1024. Off (desktop set) by default; the Android head sets it.
        /// Client-only; never sent on the wire.</summary>
        public bool SkyLowRes = false;

        /// <summary>P589 — draw the black-sky <b>moons</b> (native moon art,
        /// 0-2 per sector near the planet). Default <b>ON</b> in the black sky;
        /// <c>--moons off</c> disables. Client-only; never sent on the wire.</summary>
        public bool MoonsOn = true;

        /// <summary>P589 — draw the black-sky <b>distant galaxies / small
        /// nebulae</b> cut-outs (2-4 per sector, planet-like size). Default
        /// <b>ON</b>; <c>--distant-objects off</c> disables. Client-only.</summary>
        public bool DistantObjectsOn = true;

        /// <summary>P589 — draw the black-sky <b>deep-field galaxy field +
        /// Little Red Dots</b> (hundreds of small galaxies + a few dozen
        /// procedural red dots, one batched draw). Default <b>ON</b>;
        /// <c>--galaxies off</c> disables. Client-only.</summary>
        public bool GalaxiesOn = true;

        /// <summary>P592 — draw the black-sky <b>GIANT sky object</b> (one
        /// face-on galaxy on a curved sky patch, e.g. the Milky Way over Terra /
        /// Andromeda over Alderan). Default <b>ON</b>; <c>--giant-objects off</c>
        /// disables. Client-only.</summary>
        public bool GiantObjectsOn = true;

        /// <summary>Effective soft-particle enable: only in Enhanced — Native
        /// never constructs the enhanced smoke, so soft particles are absent
        /// there even if the (default-on) toggle is set. Pure + unit-testable.</summary>
        public static bool EffectiveSoftParticles(bool enhancedVfx, bool softParticles)
            => enhancedVfx && softParticles;

        /// <summary>P498 — the asset source (Artwork tree) the client reads real
        /// <c>.mdl</c>/<c>.cvh</c>/<c>.igc</c> files from. Null defaults to
        /// <see cref="Assets.AssetSources.Artwork"/> (the DesktopGL on-disk
        /// source over <c>ALLEGIANCE_ARTWORK_PATH</c>). A platform head — or the
        /// P498 asset-manifest recorder — supplies its own here.</summary>
        public Assets.IAssetSource? AssetSource;

        /// <summary>P500 — the durability seam for the in-app connect screen.
        /// Only the Android head supplies one; when null the connect-screen
        /// editing features (saved servers, add/edit/delete, player-name edit)
        /// are not offered and the existing server-browser behaves exactly as
        /// before (the desktop head is unchanged).</summary>
        public IConnectStore? ConnectStore;
        /// <summary>P871b — the lobby URL came from <c>--lobby</c> on the command
        /// line, so the remembered player.json value must not override it.</summary>
        public bool LobbyUrlExplicit;

        /// <summary>P500 — the soft-keyboard text-entry seam for the connect
        /// screen. Null defaults to MonoGame's platform <see cref="KeyboardInput"/>.</summary>
        public ITextPrompt? TextPrompt;

        /// <summary>P572 — a parsed live-proof automation plan (the
        /// <c>--auto-*</c> / <c>--camera</c> / <c>--hide-hud</c> /
        /// <c>--autoshots</c> / <c>--auto-dock-after</c> / <c>--exit-after</c>
        /// flags). Non-null only when at least one automation flag was passed;
        /// a normal windowed session with no flags leaves this null and behaves
        /// exactly as before (everything is off by default, client-only).</summary>
        public LiveProof.AutoProofPlan? AutoProof;

        /// <summary>P565 — the offline hull inspector (<c>--inspect</c>): boot straight
        /// into an empty sector scene and walk the core's hulls one at a time under a
        /// mouse orbit/fly-by camera, with look toggles, a VFX sandbox, F11 screenshots
        /// and a mark-bad ledger. Client-only; never connects to a server, nothing on the
        /// wire. <see cref="InspectCiv"/> / <see cref="InspectHull"/> optionally preselect
        /// the first hull.</summary>
        public bool Inspect;
        public string? InspectCiv;
        public string? InspectHull;

        /// <summary>P565 — when &gt; 0 in inspect mode, take that many real-backbuffer screenshots
        /// of successive hulls (auto-advancing every ~0.7 s) then exit. Unattended visual-proof
        /// hook (the inspect counterpart to P572's --autoshots).</summary>
        public int InspectShots;

        /// <summary>P565 — when set in inspect mode, run the scripted visual-proof showcase
        /// sequence (close-up, cloak, damage+engine+explosion, projectiles+missiles, chaos,
        /// side-by-side, fly-by approach/close/recede), screenshotting each step then exiting.</summary>
        public bool InspectDemo;

        /// <summary>P581 — inspect-only preview of the REPAIRED Meshy model for the selected
        /// hull (<c>--inspect-repaired</c>). When set, the inspector draws
        /// <c>Models/repaired/&lt;asset&gt;</c> instead of the raw Meshy model for the meshy
        /// side (side-by-side still shows native). View-only: never written to ship-art.json,
        /// so the in-game hull keeps its normal choice (Adv Scout stays native, P581).</summary>
        public bool InspectRepaired;

        /// <summary>P581 — when &gt; 0 in inspect mode, capture that many real-backbuffer
        /// screenshots of the SAME hull from evenly spaced orbit angles (each frame tilted
        /// to show the wing/strut region) then exit. Paired with <see cref="InspectRepaired"/>
        /// to sweep the raw vs repaired vs native comparison; <c>--inspect-hull</c> preselects
        /// the hull.</summary>
        public int InspectAngles;

        /// <summary>P581c — when &gt; 0 in inspect mode, override the orbit camera
        /// distance (close-up proof framing, e.g. 12) before any angle sweep.</summary>
        public float InspectDist;

        /// <summary>P615 — when set in inspect mode, walk the core's STATION types
        /// (the static core has 143) instead of ship hulls, under the same orbit /
        /// angle-sweep / screenshot / mark-bad workflow. Renders each station from
        /// its native .mdl + texture (NativeStationRenderer) or the procedural
        /// placeholder when its art is missing, side by side, at native default
        /// orientation (forward +Z, up +Y, yaw 0). <c>--inspect-stations</c>.</summary>
        public bool InspectStations;
        /// <summary>Optional exact station id or model stem to inspect alone.</summary>
        public string? InspectStation;
        /// <summary>Hold native station light time for reproducible blink proof;
        /// null keeps the live animated clock.</summary>
        public float? InspectLightTime;
        /// <summary>Orbit sweep elevation in degrees, useful for vertical station bays.</summary>
        public float InspectPitch = 22f;
        /// <summary>Capture the native model alone, centred in the orbit camera.</summary>
        public bool InspectNativeOnly;
        /// <summary>Open native vs HD material comparison on the same hull.</summary>
        public bool InspectHd;

        /// <summary>P565 — when set in inspect mode, drive the catalogue browser to this
        /// replacement-model id on the current hull (<c>--inspect-catalogue &lt;id&gt;</c>):
        /// the meshy side draws that catalogue model (repaired preferred). Sets the browser
        /// index so the run is reproducible for real-window proof. View-only unless K keeps it.</summary>
        public string? InspectCatalogue;
    }

    /// <summary>P306 capture-launch parameters (see
    /// <see cref="LaunchOptions.Capture"/>). <c>MaxFps</c> bounds the PNG
    /// write rate so disk IO does not perturb the real render-FPS
    /// measurement; when the renderer drops below it every frame is still
    /// captured (an honest slideshow), and each written frame's wall
    /// timestamp is recorded in <c>frames.tsv</c> so the encoder can
    /// reproduce the real cadence.</summary>
    public sealed class CaptureSpec
    {
        public string Host = "127.0.0.1";
        public ushort Port;
        public string PlayerName = "capture";
        public double DurationSeconds = 60;
        public string OutDir = ".";
        public double MaxFps = 30;
        public int MaxFrames = 20000;

        /// <summary>P338 — when true, the capture uses the local ship's REAL
        /// chase camera (radius-adaptive framing) instead of the fleet overview,
        /// so the live-proof frames exercise the production camera path.</summary>
        public bool Chase;
    }

    /// <summary>The top-level states of the client. Lobby is the default
    /// entry (server browser); Connecting is transient while the wire handshake
    /// runs; SideSelect (P281) is the post-connect side-choice screen between
    /// logging on and being seated; ReadyCheck (P282) is the pre-flight ready
    /// screen between seating and the mission actually starting; Flying is the
    /// joined-in-flight mode (existing behaviour).</summary>
    private enum ClientState { Lobby, Connecting, ConnectFailed, SideSelect, ReadyCheck, Flying, Disconnected, Inspecting, Intro, Splash }

    private ClientState _state;

    /// <summary>One lobby update tick: consume any completed async fetch, then
    /// (re)arm the next fetch on the cadence, and handle navigation/join keys.
    /// Runs on the main thread only.</summary>
    private void UpdateLobby(KeyboardState ks)
    {
        // Pick up a completed background fetch (result staged on the worker
        // thread; consumed here on the main thread so the list/selection is
        // mutated on the single rendering thread).
        if (_lobbyResultPending)
        {
            _lobbyResultPending = false;
            _lobbyError = _lobbyResultError;
            if (_lobbyResultData is { } data)
            {
                // P894 / LOB-0020, LOB-0026 — drop the listings this build is too
                // old to join (the advertised minClientVersion, the browse half of
                // native's LOGON version gate) and say so, pointing at the updater.
                var (visible, hiddenOld) = ServerListing.FilterForClient(data, InstalledClientVersion);
                _servers.Clear();
                _servers.AddRange(ServerListing.ExpandMissions(visible));
                // P894 / LOB-0009 — the directory's master core list (native's
                // SERVERS_LIST core array). Absent when an older lobby answers.
                _lobbyCores = _lobbyCoresStaged ?? new List<LobbyCoreEntry>();
                _lobbyCoresStaged = null;
                if (hiddenOld > 0)
                    _lobbyError = $"{hiddenOld} server(s) require a newer client (this build is {InstalledClientVersion}) - update from the lobby's update feed";
                RebuildDisplay();   // P500 — merge saved servers + fetched listings
            }
            _lobbyResultData = null;
            _lobbyResultError = null;
        }

        // P761b — a completed find-player query (staged on the worker thread;
        // native's lobby reply, gamescreen.cpp:1348-1352): select the listing the
        // queried pilot is on and close the popup, or report that nobody has it.
        if (_findPlayerResultPending)
        {
            _findPlayerResultPending = false;
            _findPlayerBusy = false;
            // P761c — consume the WORKER's staged name on the main thread; the
            // query field itself is main-thread-only now (no two-writer race).
            _findPlayerQuery = _findPlayerQueryStaged;
            _findPlayerQueryStaged = null;
            string asked = _findPlayerQuery ?? "";
            if (_findPlayerError is { } findError)
            {
                Console.WriteLine($"[monogame] find player: lobby query failed ({findError})");
                _findPlayerError = null;
            }
            int hit = ServerDirectorySearch.IndexOfListing(_displayServers, _findPlayerMatchId);
            int hitRow = hit >= 0 ? _visibleRows.IndexOf(hit) : -1;
            if (hitRow >= 0)
            {
                _selectedIndex = hitRow;
                Console.WriteLine($"[monogame] find player: '{asked}' -> {_displayServers[hit].FormatRow()}");
                _findPlayerActive = false;
                _findBuffer.Clear();
            }
            else
            {
                Console.WriteLine($"[monogame] find player: no directory game carries '{asked}'");
            }
            _findPlayerMatchId = null;
            // A hit closes the popup and forgets the name; a miss KEEPS it so the
            // popup line can say what was asked about.
            if (hitRow >= 0) _findPlayerQuery = null;
        }

        // Periodic fetch (fire-and-forget; _lobbyFetchInFlight prevents overlap).
        // P500 — only when a lobby URL is configured (221 runs no lobby, so the
        // Android head leaves it blank and polls nothing).
        if (!_lobbyFetchInFlight && _lobbyUrl.Length > 0 && _nowSeconds - _lastLobbyFetch >= LobbyRefreshSeconds)
        {
            _lastLobbyFetch = _nowSeconds;
            _lobbyFetchInFlight = true;
            _ = FetchServersAsync();
        }

        // P806 — the FILTER DIALOG (native FilterDialogPopup, gamescreen.cpp:351-529)
        // owns the keyboard while open: UP/DOWN pick a combo row, LEFT/RIGHT (or
        // SPACE) cycle its Yes/No/DontCare value, ENTER copies every combo into the
        // screen's filter state and re-filters (OnButtonOK), ESC discards the draft
        // (OnButtonCancel). Modal, like every native popup.
        if (_filterDialog.Active)
        {
            HandleLobbyFilterDialog(ks);
            return;
        }

        // P806 — open the dialog (the native Filter button; the keyboard stand-in).
        bool filtersKey = ActionDown(ks, "LobbyGameFilters");
        if (filtersKey && !_filtersKeyWasDown && !_filterActive && !_findPlayerActive)
        {
            _detailsActive = false;      // one popup at a time (native opens one dialog)
            _filterDialog.Open(_gameFilters);
            Console.WriteLine("[monogame] game filters OPEN");
        }
        _filtersKeyWasDown = filtersKey;

        // Navigation: arrows (also free pre-flight — no flight keys collide).
        // _selectedIndex is an index INTO the filtered _visibleRows, so with no
        // filter active it behaves exactly as before (every row visible).
        bool up = ActionDown(ks, "LobbyUp");        if (up && !_upWasDown && _visibleRows.Count > 0)
            _selectedIndex = (_selectedIndex - 1 + _visibleRows.Count) % _visibleRows.Count;
        _upWasDown = up;

        bool down = ActionDown(ks, "LobbyDown");
        if (down && !_downWasDown && _visibleRows.Count > 0)
            _selectedIndex = (_selectedIndex + 1) % _visibleRows.Count;
        _downWasDown = down;

        // P761 — the sort-header bar (native OnButtonBarGames,
        // gamescreen.cpp:858-898): Left/Right move the header cursor, Space
        // "clicks" it — the column becomes the LAST stack criterion (an earlier
        // entry for it is removed) and its direction toggles when it was
        // already the top entry. Suppressed while a text popup owns the keys.
        if (!_filterActive && !_findPlayerActive)
        {
            bool left = ActionDown(ks, "LobbyHeaderLeft");
            if (left && !_headerLeftWasDown) _lobbySort.MoveCursor(-1);
            _headerLeftWasDown = left;
            bool right = ActionDown(ks, "LobbyHeaderRight");
            if (right && !_headerRightWasDown) _lobbySort.MoveCursor(1);
            _headerRightWasDown = right;
            bool click = ActionDown(ks, "LobbyHeaderClick");
            if (click && !_headerClickWasDown)
            {
                _lobbySort.Click(_lobbySort.CursorColumn);
                RebuildVisibleRows();   // UpdateGameList — the list re-sorts live
                Console.WriteLine($"[monogame] lobby sort: {_lobbySort.Label()}");
            }
            _headerClickWasDown = click;
        }

        // P761 — ResetFilters (gamescreen.cpp:900-927): every filter DontCare,
        // the name substring cleared, the sort back to players reversed.
        bool resetKey = ActionDown(ks, "LobbyResetFilters");
        if (resetKey && !_lobbyResetWasDown)
        {
            _gameFilters.Reset();
            _lobbySort.ResetFilters();
            _filterActive = false;
            _filterBuffer.Clear();
            RebuildVisibleRows();
            Console.WriteLine("[monogame] lobby: filters reset, sort -> players reversed");
        }
        _lobbyResetWasDown = resetKey;

        // P761 — the find-player popup (native OnButtonFindPlayer /
        // FindDialogPopup, gamescreen.cpp:1348-1352, 531-628): P opens it, the
        // buffer takes typed text, Enter searches the directory's player lists
        // and selects the game the pilot sits in, Esc closes it (Escape chain).
        bool findWasActive = _findPlayerActive;   // frame-start state (see the join gate below)
        bool pKey = ActionDown(ks, "LobbyFindPlayer");
        if (pKey && !_findPWasDown)
        {
            _findPlayerActive = !_findPlayerActive;
            if (!_findPlayerActive) _findBuffer.Clear();
            else { _detailsActive = false; _findPlayerQuery = null; }
            Console.WriteLine($"[monogame] find player {( _findPlayerActive ? "OPEN" : "closed" )}");
        }
        _findPWasDown = pKey;

        if (_findPlayerActive)
        {
            bool fback = ks.IsKeyDown(XnaKeys.Back);
            if (fback && !_findBackWasDown && _findBuffer.Length > 0)
                _findBuffer.Length -= 1;
            _findBackWasDown = fback;

            // Enter (LobbyJoin shares the physical key): ASK THE LOBBY about this
            // one pilot (native OnButtonFindPlayer → FM_C_FIND_PLAYER,
            // gamescreen.cpp:1348-1352). The directory answers with the server the
            // pilot is on — it does NOT publish rosters on the open listing — and
            // the reply is consumed at the top of this method.
            bool fenter = ActionDown(ks, "LobbyJoin");
            if (fenter && !_findEnterWasDown && !_findPlayerBusy)
            {
                if (ServerDirectorySearch.FindPlayerRequestPath(_findBuffer.ToString()) is { } path)
                {
                    _findPlayerBusy = true;
                    _ = QueryFindPlayerAsync(_findBuffer.ToString(), path);
                }
                else
                {
                    Console.WriteLine("[monogame] find player: type a pilot name first");
                }
            }
            _findEnterWasDown = fenter;
        }

        // P761 — the details popup (native OnButtonDetails, gamescreen.cpp:
        // 1308-1326): I toggles the panel for the selected game.
        bool iKey = ActionDown(ks, "LobbyDetails");
        if (iKey && !_detailsIWasDown && !_findPlayerActive && _visibleRows.Count > 0)
        {
            _detailsActive = !_detailsActive;
            if (_detailsActive) Console.WriteLine("[monogame] details popup OPEN");
        }
        _detailsIWasDown = iKey;

        // Manual retry: R forces an immediate refetch (helps an unreachable lobby).
        bool rRetry = ActionDown(ks, "LobbyRefresh");
        if (rRetry && !_rWasDown)
        {
            _lastLobbyFetch = double.NegativeInfinity;
            _lobbyError = null;
        }
        _rWasDown = rRetry;

        // P274 — F toggles the text filter (mnemonic: Find, native's
        // FindDialogPopup). Opening preserves any existing buffer; closing
        // clears it. The opening F keystroke never leaks into the buffer:
        // MonoGame pumps Window.TextInput events BEFORE this frame's Update
        // poll, so when the 'f' event fires _filterActive is still false and
        // OnTextInput's gate drops it.
        bool fKey = ActionDown(ks, "LobbyFilter");
        if (fKey && !_fWasDown)
        {
            _filterActive = !_filterActive;
            if (!_filterActive)
                _filterBuffer.Clear();
            RebuildVisibleRows();
            Console.WriteLine($"[monogame] lobby filter {( _filterActive ? $"ON '{_filterBuffer}'" : "OFF" )}");
        }
        _fWasDown = fKey;

        // While filtering, Backspace deletes the previous character and live-
        // narrows the list (mirrors the in-flight chat backspace handling).
        // TEXT ENTRY — an allow-listed raw key (it only edits the filter line).
        if (_filterActive)
        {
            bool back = ks.IsKeyDown(XnaKeys.Back);
            if (back && !_backWasDown && _filterBuffer.Length > 0)
            {
                _filterBuffer.Length -= 1;
                RebuildVisibleRows();
            }
            _backWasDown = back;
        }

        // Enter joins the highlighted server (shared by the CONNECT button and
        // the gamepad A button via TryJoinSelected). On join failure we revert
        // to the lobby with a message rather than exiting.
        bool enter = ActionDown(ks, "LobbyJoin");
        // P761 — while the find-player popup is up (at frame start OR still up
        // after this frame's search), Enter runs the search — never a join in
        // the same keystroke.
        if (enter && !_enterWasDown && !findWasActive && !_findPlayerActive)
            TryJoinSelected();
        _enterWasDown = enter;

        // P500 — the connect screen's touch taps and gamepad actions (rows /
        // bottom buttons). Offered only when a connect store is present
        // (Android); the desktop head is unchanged.
        if (_connectStore is not null)
        {
            HandleConnectGamepad();
            HandleConnectTouch();
        }
    }

    /// <summary>P806 — one frame of the server list's filter dialog (native
    /// <c>FilterDialogPopup</c>, gamescreen.cpp:351-529). Runs only while the
    /// popup is open; the screen's own keys are suppressed for that frame.
    /// <para>UP/DOWN move the combo row; LEFT/RIGHT step the selected value
    /// (RIGHT/SPACE forward through Yes→No→DontCare, LEFT backward); ENTER is
    /// OnButtonOK (copy every combo into <see cref="_gameFilters"/> and re-filter
    /// via UpdateGameList); ESC is OnButtonCancel (discard the draft).</para></summary>
    private void HandleLobbyFilterDialog(KeyboardState ks)
    {
        bool up = ActionDown(ks, "LobbyUp");
        if (up && !_fdUpWasDown) _filterDialog.MoveRow(-1);
        _fdUpWasDown = up;
        bool down = ActionDown(ks, "LobbyDown");
        if (down && !_fdDownWasDown) _filterDialog.MoveRow(1);
        _fdDownWasDown = down;

        bool left = ActionDown(ks, "LobbyHeaderLeft");
        if (left && !_fdLeftWasDown)
            _filterDialog.CycleBackward(_filterDialog.Row);
        _fdLeftWasDown = left;
        bool right = ActionDown(ks, "LobbyHeaderRight");
        bool space = ActionDown(ks, "LobbyHeaderClick");
        if ((right && !_fdRightWasDown) || (space && !_fdSpaceWasDown))
            _filterDialog.Cycle(_filterDialog.Row);
        _fdRightWasDown = right;
        _fdSpaceWasDown = space;

        bool enter = ActionDown(ks, "LobbyJoin");
        if (enter && !_fdEnterWasDown)
        {
            _filterDialog.Commit(_gameFilters);
            RebuildVisibleRows();   // OnButtonOK → UpdateGameList
            // Keep the shared join latch "down" so the OK keystroke cannot also
            // join the selected server on the next frame.
            _enterWasDown = true;
            Console.WriteLine($"[monogame] game filters applied; {_visibleRows.Count}/{_displayServers.Count} shown");
            return;
        }
        _fdEnterWasDown = enter;

        bool esc = ActionDown(ks, "LobbyClearFilter");
        if (esc && !_fdEscWasDown)
        {
            _filterDialog.Cancel();
            Console.WriteLine("[monogame] game filters cancelled");
            _enterWasDown = ActionDown(ks, "LobbyJoin");
        }
        _fdEscWasDown = esc;
    }

    /// <summary>Rebuild the <see cref="_visibleRows"/> index list from
    /// <see cref="_displayServers"/> (saved servers + fetched lobby listings)
    /// under the current filter text, then re-clamp the selection into it.
    /// Called whenever the combined list or the filter text changes (fetch
    /// result, F toggle, typing via OnTextInput, Backspace, connect edits).
    /// Always leaves <see cref="_selectedIndex"/> in range.</summary>
    private void RebuildVisibleRows()
    {
        string needle = _filterActive ? _filterBuffer.ToString() : string.Empty;
        // P761 — the substring feeds the native-shaped filter model (the only
        // filter the port's server records can evaluate — see LobbyGameFilters).
        _gameFilters.NameSubstring = needle;
        _visibleRows.Clear();
        for (int i = 0; i < _displayServers.Count; i++)
            if (_gameFilters.Apply(_displayServers[i]))
                _visibleRows.Add(i);
        // P761 — apply the sort stack (UpdateGameList, gamescreen.cpp:1185-1238:
        // one stable ordered pass per criterion in stack order, so the LAST
        // criterion is the primary key and earlier ones break its ties).
        var sorted = _lobbySort.Apply(_visibleRows, (idx, column) =>
            LobbyGameSort.ListingKey(_displayServers[idx], column));
        _visibleRows.Clear();
        _visibleRows.AddRange(sorted);
        _selectedIndex = _visibleRows.Count > 0
            ? Math.Clamp(_selectedIndex, 0, _visibleRows.Count - 1)
            : 0;
    }

    /// <summary>Close the lobby filter and forget its text (Esc while filtering,
    /// or the off-half of the F toggle).</summary>
    private void ClearFilter()
    {
        _filterActive = false;
        _filterBuffer.Clear();
        RebuildVisibleRows();
        Console.WriteLine("[monogame] lobby filter cleared");
    }

    /// <summary>P761b — the lobby find-player round trip: ask the directory about
    /// exactly one pilot (<c>GET /api/servers/findplayer?name=</c>) and stage the
    /// answering listing's id for the main thread. Never throws; a failure is
    /// logged and treated as "no answer".</summary>
    private async Task QueryFindPlayerAsync(string name, string path)
    {
        string? matchId = null;
        string? error = null;
        try
        {
            var hits = await _http.GetFromJsonAsync<List<FindPlayerResult>>(path,
                ServerListing.JsonOptions).ConfigureAwait(false);
            matchId = hits is { Count: > 0 } ? hits[0].Id : null;
        }
        catch (Exception ex)
        {
            error = $"{_lobbyUrl}: {ex.Message}";
        }
        // Volatile write AFTER staging the fields (same pairing as the fetch).
        // P761c — stage the name in the worker-only field; the main thread copies
        // it to _findPlayerQuery when it consumes the reply. Never write the
        // main thread's field from here.
        _findPlayerMatchId = matchId;
        _findPlayerQueryStaged = name;
        _findPlayerError = error;
        _findPlayerResultPending = true;
    }

    /// <summary>HTTP GET the lobby's <c>/api/servers</c> and stage the result for
    /// the main thread (see <see cref="UpdateLobby"/>). Standard
    /// <see cref="HttpClient"/> — no MonoGame networking involved. Never throws;
    /// failures become an on-screen error and the cadence just retries.</summary>
    private async Task FetchServersAsync()
    {
        List<ServerListing>? data = null;
        List<LobbyCoreEntry>? cores = null;
        string? error = null;
        try
        {
            data = await _http.GetFromJsonAsync<List<ServerListing>>(
                "/api/servers", ServerListing.JsonOptions).ConfigureAwait(false);
            // P894 / LOB-0009 — the master core list the directory relays with
            // every server-list request in native (SERVERS_LIST's core array).
            // A lobby without the endpoint just yields no summary.
            try
            {
                var list = await _http.GetFromJsonAsync<LobbyCoreList>(
                    "/api/servers/cores", ServerListing.JsonOptions).ConfigureAwait(false);
                cores = list?.Cores is { } c ? new List<LobbyCoreEntry>(c) : null;
            }
            catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException
                                           or NotSupportedException or TaskCanceledException)
            {
                cores = null;   // pre-P894 lobby: no core list advertised
            }
        }
        catch (Exception ex)
        {
            error = $"Lobby unreachable ({_lobbyUrl}): {ex.Message} - retrying...";
        }

        // Volatile write AFTER filling the data so the main thread's volatile
        // read of _lobbyResultPending pairs correctly.
        _lobbyResultData = data;
        _lobbyCoresStaged = cores;
        _lobbyResultError = error;
        _lobbyResultPending = true;
        _lobbyFetchInFlight = false;
    }

    /// <summary>Draw the server-browser / connect screen: title, the combined
    /// row list (saved servers first, then lobby listings), the highlighted
    /// row, an error/empty banner, the bottom action buttons (when a connect
    /// store is present), and a footer of the controls. Self-contained
    /// SpriteBatch session. Same rendering style for the desktop browser and
    /// the Android connect screen; the connect-screen chrome only appears when
    /// the store is supplied.</summary>
    private void DrawLobby()
    {
        var vp = GraphicsDevice.Viewport;
        float w = vp.Width;
        float h = vp.Height;
        bool connect = _connectStore is not null;
        bool polling = _lobbyUrl.Length > 0;
        // P761 — the sort-header bar text (composed in DrawLobby; the pure
        // stack state lives in LobbyGameSort).
        string headerText = _lobbySort.HeaderText();

        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            var titleCol = new Color(HudColor.TealAccent.R, HudColor.TealAccent.G, HudColor.TealAccent.B);
            var dimCol = new Color(0.48f, 0.64f, 0.62f);
            var textCol = new Color(0.88f, 0.96f, 0.95f);
            var savedCol = new Color(0.70f, 0.95f, 0.75f);

            // P761b — a fixed one-line-each band above the list, so nothing
            // overprints: title (20) / polling state (52) / player line (74,
            // connect only) / sort-header bar (98) / filter-or-find line (122) /
            // banner (146). Rows start at ConnectScreenLayout.ListTop (150).
            var title = "SERVER BROWSER";
            var tsz = _hudBig.MeasureString(title);
            _spriteBatch.DrawString(_hudBig, title, new Vector2((w - tsz.X) / 2f, 16f), titleCol);

            string sub = polling ? $"Polling {_lobbyUrl}/api/servers" : "Manual connect (no lobby)";
            var ssz = _hudSmall.MeasureString(sub);
            _spriteBatch.DrawString(_hudSmall, sub, new Vector2((w - ssz.X) / 2f, 52f), dimCol);

            // P894 / LOB-0009 — the master core list native relays in SERVERS_LIST
            // (lobby/client.cpp:377-385): the cores a player may pick before
            // joining, with how many live servers serve each.
            if (polling && !connect && _lobbyCores.Count > 0)
            {
                string coreText = "CORES: " + string.Join(", ", _lobbyCores.Take(4).Select(c => c.File + (c.Servers > 0 ? $" ({c.Servers})" : "")))
                    + (_lobbyCores.Count > 4 ? $" +{_lobbyCores.Count - 4} more" : "");
                coreText = Rendering.TeamPaneRenderer.Fit(coreText, _hudSmall, w - 120f);
                var csz = _hudSmall.MeasureString(coreText);
                _spriteBatch.DrawString(_hudSmall, coreText, new Vector2((w - csz.X) / 2f, 74f),
                    new Color(0.62f, 0.80f, 0.72f));
            }

            if (connect)
            {
                string nameLine = $"Player: {_playerName}   (tap NAME to change)";
                _spriteBatch.DrawString(_hudSmall, nameLine, new Vector2(60f, 74f), textCol);
            }

            // Banner (error / empty) — one line, so it cannot collide with the
            // sort header above it or the first row below.
            string? banner = null;
            Color bannerCol = dimCol;
            if (_lobbyError is { } err)
            {
                banner = polling ? err + "  (R retries now)" : err;
                bannerCol = new Color(1f, 0.45f, 0.35f);
            }
            else if (connect && _savedServers.Count == 0 && !polling)
                banner = "No saved servers yet - tap ADD to create one.";
            else if (polling && _displayServers.Count == 0)
                banner = "No servers found yet - waiting for registrations...";
            else if (_filterActive && _visibleRows.Count == 0)
            {
                banner = $"No servers match '{_filterBuffer}'.";
                bannerCol = new Color(1f, 0.6f, 0.4f);
            }
            if (banner is not null)
                _spriteBatch.DrawString(_hudSmall, banner, new Vector2(60f, 146f), bannerCol);

            // P274 — the live filter/edit line. Drawn above the list so the
            // narrowed selection is read against it. '_' is a blinking cursor
            // stand-in (fonts bake ASCII 32..126 only, no box-drawing glyphs).
            if (_filterActive)
            {
                string prompt = "Filter: " + _filterBuffer.ToString() + "_";
                var psz = _hudSmall.MeasureString(prompt);
                _spriteBatch.DrawString(_hudSmall, prompt, new Vector2(60f, 122f),
                    new Color(0.98f, 1.0f, 0.65f));
                string cnt = $"{_visibleRows.Count}/{_displayServers.Count}";
                _spriteBatch.DrawString(_hudSmall, cnt, new Vector2(60f + psz.X + 24f, 125f), dimCol);
            }

            // P761 — the sort-header bar (native's clickable game-list header,
            // gamescreen.cpp:858-898): one cursor cell, stack membership and
            // direction shown per column. Only while no popup owns the keys.
            if (polling)
            {
                _spriteBatch.DrawString(_hudSmall, headerText, new Vector2(56f, 98f),
                    new Color(0.55f, 0.85f, 0.82f));
            }

            // P761 — the find-player popup line (native's FindDialogPopup edit
            // box): typed player name, Enter searches the directory's games.
            // Shares the filter-line slot (never both active at once).
            if (_findPlayerActive)
            {
                string state = _findPlayerBusy ? "  (searching the lobby...)"
                    : _findPlayerQuery is { } asked
                        ? $"  (no listed game carries '{ScoreboardLayout.AsciiSafe(asked)}')"
                        : "  (ENTER asks the lobby, ESC close)";
                string fp = "Find player: " + _findBuffer.ToString() + "_" + state;
                _spriteBatch.DrawString(_hudSmall, fp, new Vector2(60f, 122f),
                    new Color(0.98f, 0.85f, 1.0f));
            }

            // P761 — the details popup (native OnButtonDetails' popup): the
            // selected game's genuine directory fields. P761b — sized and
            // positioned FIRST so the list can end short of it: the panel sits in
            // its own right-hand column and the rows/highlight stop at its edge,
            // so it can never cover the selected row. Drawn after the rows.
            Rectangle detailsPanel = default;
            float rowRight = 50f + w * ConnectScreenLayout.RowWidthFactor;
            if (_detailsActive && _visibleRows.Count > 0)
            {
                var sel = _displayServers[_visibleRows[Math.Clamp(_selectedIndex, 0, _visibleRows.Count - 1)]];
                var lines = ServerDirectorySearch.DetailsLines(sel);
                float bh = 30f + lines.Count * _hudSmall.LineSpacing + 12f;
                float bw = MathF.Min(400f, w * 0.34f);
                float bx = w - bw - 40f;
                detailsPanel = new Rectangle((int)bx, (int)ConnectScreenLayout.ListTop, (int)bw, (int)bh);
                rowRight = MathF.Min(rowRight, bx - 12f);
            }

            // Combined rows (saved servers first, then lobby listings).
            for (int i = 0; i < _visibleRows.Count; i++)
            {
                int di = _visibleRows[i];
                var s = _displayServers[di];
                float y = ConnectScreenLayout.ListTop + i * ConnectScreenLayout.RowH;
                bool sel = i == _selectedIndex;
                bool isSaved = di < _savedServers.Count;
                _spriteBatch.Draw(_whitePixel, new Rectangle(50, (int)y, (int)(rowRight - 50f), (int)(ConnectScreenLayout.RowH - 6f)),
                    sel ? new Color(0.08f, 0.35f, 0.33f, 0.55f) : new Color(0f, 0f, 0f, 0f));

                string prefix = isSaved ? "[S] " : "";
                string suffix = "";
                if (isSaved && _lastUsedKey is { } lk && _savedServers[di].HostPort == lk)
                    suffix = "  (last used)";
                string line = prefix + RowText(s) + suffix;
                // P761b — fit the row to the band so a long name never runs
                // under (or past) the details panel.
                line = Rendering.TeamPaneRenderer.Fit(line, _hudSmall, rowRight - 56f);
                _spriteBatch.DrawString(_hudSmall, line, new Vector2(56f, y + 2f),
                    sel ? titleCol : (isSaved ? savedCol : textCol));
            }

            // P761 — the details popup itself, drawn ON TOP of the (shortened)
            // row band: native OnButtonDetails' panel, gamescreen.cpp:1308-1326.
            // Hidden while the modal filter dialog is open: the two overlapped.
            if (detailsPanel != default && !_filterDialog.Active)
            {
                var sel = _displayServers[_visibleRows[Math.Clamp(_selectedIndex, 0, _visibleRows.Count - 1)]];
                var lines = ServerDirectorySearch.DetailsLines(sel);
                _spriteBatch.Draw(_whitePixel, detailsPanel, new Color(0.014f, 0.020f, 0.029f, 0.96f));
                _spriteBatch.Draw(_whitePixel, new Rectangle(detailsPanel.X, detailsPanel.Y, detailsPanel.Width, 2),
                    new Color(0.42f, 0.72f, 0.95f));
                _spriteBatch.DrawString(_hudSmall, "GAME DETAILS  (I close)",
                    new Vector2(detailsPanel.X + 14f, detailsPanel.Y + 8f), titleCol);
                for (int li = 0; li < lines.Count; li++)
                    _spriteBatch.DrawString(_hudSmall, lines[li],
                        new Vector2(detailsPanel.X + 14f, detailsPanel.Y + 32f + li * _hudSmall.LineSpacing), textCol);
            }

            // P806 — the FILTER DIALOG (native FilterDialogPopup): the eleven
            // Yes/No/DontCare combos, drawn over the browser as a centred panel
            // while open. Modal, so it is the last thing painted.
            if (_filterDialog.Active)
            {
                var lines = _filterDialog.Text().Split('\n');
                float lh = _hudSmall.LineSpacing;
                float bw = MathF.Min(560f, w * 0.62f);
                float bh = 18f + lines.Length * lh + 12f;
                float bx = (w - bw) / 2f;
                float by = MathF.Max(40f, (h - bh) / 2f);
                _spriteBatch.Draw(_whitePixel, new Rectangle((int)bx, (int)by, (int)bw, (int)bh),
                    new Color(0.014f, 0.020f, 0.029f, 0.97f));
                _spriteBatch.Draw(_whitePixel, new Rectangle((int)bx, (int)by, (int)bw, 2),
                    new Color(0.42f, 0.72f, 0.95f));
                for (int li = 0; li < lines.Length; li++)
                    _spriteBatch.DrawString(_hudSmall, lines[li],
                        new Vector2(bx + 14f, by + 10f + li * lh),
                        li == 1 + _filterDialog.Row ? new Color(0.98f, 1.0f, 0.65f) : textCol);
            }

            // P500 — bottom action buttons (connect store only).
            if (connect)
            {
                foreach (var (kind, rect) in ConnectScreenLayout.ComputeButtons(w, h))
                {
                    bool primary = kind == ConnectScreenLayout.Button.Connect;
                    _spriteBatch.Draw(_whitePixel, new Rectangle((int)rect.X, (int)rect.Y, (int)rect.W, (int)rect.H),
                        primary ? new Color(0.10f, 0.40f, 0.38f, 0.85f) : new Color(0.045f, 0.095f, 0.10f, 0.82f));
                    string label = ConnectScreenLayout.ButtonDefs[(int)kind].Label;
                    var lsz = _hudSmall.MeasureString(label);
                    _spriteBatch.DrawString(_hudSmall, label,
                        new Vector2(rect.CenterX - lsz.X / 2f, rect.CenterY - lsz.Y / 2f),
                        primary ? titleCol : dimCol);
                }
            }

            string footer = connect
                ? "TAP a row or BUTTON   A/CONNECT join   Y ADD   X EDIT   B back"
                : (_filterActive
                    ? "TYPE to filter   BACKSPACE delete   UP/DOWN select   ENTER join   ESC clear"
                    // P761b — kept inside the viewport: the old wording ran off
                    // both edges at 1280 wide.
                    : "UP/DOWN   ENTER join   F find   L filters   P player   I details   C reset   ARROWS+SPACE sort   R refresh   ESC");
            var fsz = _hudSmall.MeasureString(footer);
            // P500 — on the connect screen the footer anchors above the bottom
            // safe-area inset (Android clips the bottom ~6%); the desktop path
            // keeps its original position exactly.
            float footerY = connect ? h - ConnectScreenLayout.SafeBottom(h) + 6f : h - 40f;
            _spriteBatch.DrawString(_hudSmall, footer, new Vector2((w - fsz.X) / 2f, footerY), dimCol);
        }
        finally
        {
            _spriteBatch.End();
        }
    }

    /// <summary>A brief "connecting…" screen while the wire handshake completes
    /// (it is synchronous, so this is a single rendered frame — a nicety, not
    /// a spinner).</summary>
    private void DrawConnecting()
    {
        var vp = GraphicsDevice.Viewport;
        float w = vp.Width;
        float h = vp.Height;

        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            string host = string.IsNullOrEmpty(_connectHost) ? "127.0.0.1" : _connectHost;
            string msg = $"Connecting to {host}:{_connectPort} as '{_playerName}'...";
            var sz = _hudBig.MeasureString(msg);
            _spriteBatch.DrawString(_hudBig, msg, new Vector2((w - sz.X) / 2f, (h - sz.Y) / 2f),
                new Color(0.88f, 0.96f, 0.95f));
        }
        finally
        {
            _spriteBatch.End();
        }
    }

    /// <summary>
    /// P759 — the connect-failure screen (native OnLogonGameServerFailed's
    /// retry/abort decision): shows the failure reason and the two keys.
    /// </summary>
    private void DrawConnectFailed()
    {
        var vp = GraphicsDevice.Viewport;
        float w = vp.Width;
        float h = vp.Height;
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            var title = "CONNECT FAILED";
            var tsz = _hudBig.MeasureString(title);
            _spriteBatch.DrawString(_hudBig, title, new Vector2((w - tsz.X) / 2f, h / 2f - 60f),
                new Color(1f, 0.62f, 0.35f));
            string reason = _connectFailureReason ?? "unknown failure";
            var rsz = _hudSmall.MeasureString(reason);
            _spriteBatch.DrawString(_hudSmall, reason, new Vector2((w - rsz.X) / 2f, h / 2f - 18f),
                new Color(0.88f, 0.96f, 0.95f));
            // P894 — a refused old client is told how to fix it, not just why it
            // failed (native sent AUTO_UPDATE_INFO and dropped the connection,
            // lobby/client.cpp:272-288).
            if (reason.Contains("update", StringComparison.OrdinalIgnoreCase))
            {
                const string updateHint = "Restart the client: the lobby's update feed installs the required build.";
                var usz = _hudSmall.MeasureString(updateHint);
                _spriteBatch.DrawString(_hudSmall, updateHint, new Vector2((w - usz.X) / 2f, h / 2f + 44f),
                    new Color(0.94f, 0.82f, 0.42f));
            }
            string hint = $"{KeyOf("LobbyRefresh").Label} retry    {KeyOf("MainMenu").Label} abort";
            var hsz = _hudSmall.MeasureString(hint);
            _spriteBatch.DrawString(_hudSmall, hint, new Vector2((w - hsz.X) / 2f, h / 2f + 22f),
                new Color(0.62f, 0.74f, 0.74f));
        }
        finally
        {
            _spriteBatch.End();
        }
    }

    /// <summary>P320 — the session-loss screen: a clear, full-panel
    /// "CONNECTION LOST" indication over the (now frozen) world, replacing the
    /// old silent frame-identical freeze with an unmistakable state.</summary>
    private void DrawConnectionLost()
    {
        _connectionLostRenderer.Draw(GraphicsDevice.Viewport);
    }

    /// <summary>Establish the real wire connection + logon handshake for the
    /// chosen server (the exact logic that used to run unconditionally in
    /// <see cref="Initialize"/>). On success flips to <see cref="ClientState.Flying"/>;
    /// on failure either reverts to the lobby (when joined from there) or exits
    /// (the unchanged direct-connect path).</summary>
    private uint _connectMissionCookie = uint.MaxValue;

    private void BeginConnect(string host, ushort port, uint missionCookie = uint.MaxValue)
    {
        _moderation.Reset();
        // P894 — the reconnect/resume logon reports the same client build as the
        // first logon so the server's minimum-client-version gate answers both.
        _session.ClientVersion = InstalledClientVersion;
        _state = ClientState.Connecting;
        // P500 — record the ACTIVE target here, not just from LaunchOptions.
        // Every join (lobby, connect screen, resume) remembers where we are, so
        // the P499 resume-reconnect (NotifyResumed) and the connect screen's
        // last-used memory both work regardless of entry path.
        _connectMissionCookie = missionCookie;
        _connectHost = host;
        _connectPort = port;
        // P789 — remote matches record their performance too (no wire change:
        // everything the recorder sees, the client already measures).
        if (_skirmish is null && _benchmark is null && !_sessionRecording)
            StartSessionRecording(SessionMatchKind.Normal);
        if (_skirmish is null && _connectStore is not null && !string.IsNullOrEmpty(host))
        {
            _lastUsedKey = $"{host}:{port}";
            PersistConnectProfile();
        }
        Console.WriteLine($"[monogame] connecting to {host}:{port} as '{_playerName}' ...");
        try
        {
            // P813c (EXPERIMENTAL) — opt into the reconnect grace only when the
            // operator asks for it. The window must match the SERVER's
            // reconnect.graceSeconds (the server also needs reconnect.enabled=true);
            // with the variable unset nothing changes (today's session-lost path).
            if (ReconnectGraceSecondsFromEnvironment() is { } reconnectWindow)
            {
                _session.EnableAutoReconnect(reconnectWindow);
                Console.WriteLine($"[monogame] P813c reconnect grace armed: window {reconnectWindow:0.##}s "
                    + "(auto-reconnect + resume token; server needs reconnect.enabled=true)");
            }
            _session.Connect(port, _playerName, host);
            var logon = Request(_playerName);
            logon.DwCookie = missionCookie;
            if (_skirmish is null && _identityClient?.Account is not null)
            {
                var listing = _servers.FirstOrDefault(s => s.Address == host && s.Port == port && s.Verified);
                string? serverId = listing?.IdentityServerId;
                if (serverId is null)
                    lock (_introFoundLock) serverId = _introFound.FirstOrDefault(s => s.Address == host && s.Port == port && s.Verified)?.IdentityServerId;
                if (serverId is not null)
                    logon.ZoneTicket = System.Text.Encoding.UTF8.GetBytes(_identityClient.JoinTokenAsync(serverId).GetAwaiter().GetResult());
            }
            _session.SendLogonRequest(logon);
            var ack = _session.WaitForAck(5000);
            // P894 / LOB-0020, LOB-0026 — a refused logon (the server's minimum
            // client version, the API version, a duplicate name, a reserved
            // callsign, the file-set CRC…) carries the reason the player must see.
            // Before this the client ignored FValidated and walked into a side
            // select it could never complete.
            if (!ack.FValidated)
                throw new InvalidOperationException(ack.CharNameOrFailureReason is { Length: > 0 } why
                    ? why
                    : "the server refused the logon");
            Console.WriteLine($"[monogame] logged on: serverTime={ack.TimeServer}");
            // P281 — the server now defers our spawn to a post-connect side-select
            // step: after LOGONACK it pushes a SIDE_ROSTER(211) snapshot and holds
            // the peer (no ship, no SET_CLUSTER) until we send FM_C_POSITIONREQ(39).
            // Enter the side-select screen here; UpdateSideSelect sends POSITIONREQ
            // and transitions to Flying once the server seats us (SET_CLUSTER sets
            // World.MyShipId).
            _state = ClientState.SideSelect;
            if (_benchmark is null) ObserveBenchmarkSharingSession();
            _sideSelectIndex = 0;
            _sideSelectFromGameOver = false;   // P481 — a fresh logon is never an eliminated-side lobby
        }
        catch (Exception ex)
        {
            // P759 — native OnLogonGameServerFailed (introscreen.cpp:1667-1699)
            // presents a retry/abort decision instead of silently bouncing back.
            // Stage the failure on a dedicated screen; R retries the same
            // host/port, Esc aborts to wherever the join was launched from.
            Console.WriteLine($"[monogame] FAILED to connect/log on: {ex.Message}");
            _connectFailureReason = $"Could not join {host}:{port} - {ex.Message}";
            _state = ClientState.ConnectFailed;
        }
    }

    /// <summary>
    /// P759 — the connect-failure screen's abort half (native
    /// <c>OnLogonGameServerFailed(false, reason)</c> → message box, then back to
    /// the origin): intro when launched from the intro find-server flow, lobby
    /// when joined from the browser, else exit.
    /// </summary>
    private void AbortConnect()
    {
        string reason = _connectFailureReason ?? "unknown connect failure";
        _connectFailureReason = null;
        switch (ConnectScreenLayout.AbortDestination(_returnToIntroOnFailure, _returnToLobbyOnFailure))
        {
            case ConnectScreenLayout.ConnectAbortDestination.Intro:
                _returnToIntroOnFailure = false;
                if (_skirmish is not null) { StopSkirmish(); _skirmishError = reason; }
                _state = ClientState.Intro;
                _introFindError = reason;
                break;
            case ConnectScreenLayout.ConnectAbortDestination.Lobby:
                _returnToLobbyOnFailure = false;
                _state = ClientState.Lobby;
                _lobbyError = reason;
                break;
            default:
                Exit();
                break;
        }
    }

    /// <summary>
    /// P759 — drive the connect-failure screen: R retries the same host/port
    /// (native <c>OnLogonGameServerFailed(bRetry=true)</c> re-opens the logon
    /// flow), Esc aborts (<see cref="AbortConnect"/>). Edge-triggered via the
    /// action map so a rebind is honoured.
    /// </summary>
    private void UpdateConnectFailed(KeyboardState ks)
    {
        bool retry = ActionDown(ks, "LobbyRefresh");
        if (retry && !_connectRetryWasDown)
        {
            Console.WriteLine($"[monogame] connect-failure: retrying {_connectHost}:{_connectPort}");
            _connectFailureReason = null;
            BeginConnect(_connectHost!, _connectPort, _connectMissionCookie);
            return;
        }
        _connectRetryWasDown = retry;

        bool abort = ActionDown(ks, "MainMenu") || ActionDown(ks, "MenuBack");
        if (abort && !_connectAbortWasDown)
        {
            Console.WriteLine("[monogame] connect-failure: abort");
            AbortConnect();
        }
        _connectAbortWasDown = abort;
    }

    // ── P499 — Android lifecycle seams ──────────────────────────────────────
    /// <summary>True when this launch carries a direct-connect target
    /// (host/port), i.e. the head chose a server rather than the lobby. Used
    /// by the Android head to know a reconnect is meaningful on resume.</summary>
    public bool HasDirectTarget => _connectHost is not null;

    /// <summary>P499 — the app went to the background (or is being destroyed):
    /// close the wire session cleanly NOW so the server sees a proper logoff /
    /// disconnect instead of a silently dead socket. The host/port stay
    /// remembered in <c>_connectHost</c>/<c>_connectPort</c> so
    /// <see cref="NotifyResumed"/> can reconnect (P480 keeps players seated
    /// across rotation; this keeps the SOCKET honest across pause).</summary>
    public void NotifyPaused()
    {
        if (_session is not null && _session.Connected)
        {
            Console.WriteLine("[monogame] lifecycle PAUSED: closing connection cleanly");
            _session.Shutdown();
        }
    }

    /// <summary>P499 — the app returned to the foreground: offer reconnect to
    /// the direct-connect target that was active on pause. Safe to call
    /// repeatedly; no-ops when there is no direct target or a session is
    /// already live. A failed reconnect reverts to the server browser rather
    /// than hard-exiting (the direct-connect desktop default), which is the
    /// friendlier Android behaviour on a flaky resume.</summary>
    public void NotifyResumed()
    {
        if (_connectHost is not null && (_session is null || !_session.Connected))
        {
            Console.WriteLine($"[monogame] lifecycle RESUMED: reconnecting to {_connectHost}:{_connectPort}");
            bool prior = _returnToLobbyOnFailure;
            _returnToLobbyOnFailure = true;   // a resume-reconnect failure must not exit the app
            try { BeginConnect(_connectHost, _connectPort, _connectMissionCookie); }
            finally { _returnToLobbyOnFailure = prior; }
        }
    }

    // ── P214 — chat ──────────────────────────────────────────────────────────
    /// <summary>Sends the composed line to the current <see cref="_chatTarget"/>
    /// (default Team, P456) — one chat per subject for an individual/group
    /// recipient (P818) — and closes the input.</summary>
    private readonly ModerationModel _moderation = new();

    private void SendChatLine()
    {
        string text = _chatBuffer.ToString().Trim();
        // P784 — every send ends in native QuitComposing (console.cpp:1618-1620):
        // the state resets and the typed text clears with it.
        SetComposeState(NativeComposeState.NotComposing);
        if (text.Length == 0)
            return;
        lock (_session.Gate)
        {
            if (_moderation.Handle(text, _session.World, out var boot))
            {
                if (boot is { } id) _session.SendQuitMission(id, QuitSideReason.LeaderBooted);
                return;
            }
        }
        // P871 — the HTTP lobby's directory channel (native's lobby-session chat
        // relay); "/lobby <text>" posts there instead of the game channel.
        if (text.StartsWith("/lobby ", StringComparison.OrdinalIgnoreCase))
        {
            SendDirectoryChat(text[7..].Trim());
            return;
        }
        Console.WriteLine($"[monogame] chat ({_chatTarget}) >> {text}");
        // P456: send to the compose's current target, not a hardcoded Everyone —
        // the recipient is client state (_chatTarget) so Tab cycling works and
        // the target persists across compose sessions. commandId=c_cidNone marks
        // a typed chat as carrying no order (P457), so the server never queues
        // one from it.
        // P784 — native BaseClient::SendChat converts a NA recipient to the
        // sender's own ids (clintlib.cpp:2312-2367): Individual → own ship id,
        // Wing → own wing id, Team → own side id, FriendlySector/AllSector →
        // the current chat cluster. The port's server routes Individual on
        // OidRecipient (ship id), Wing on the wing id and FriendlySector on the
        // sector id, so the compose fills all three from the world.
        short oidRecipient = 0;
        short selfId = -1;
        IReadOnlyList<Rendering.ChatSendPlan.Send> plan = [];
        lock (_session.Gate)
        {
            var world = _session.World;
            // P818 — native SendChatInternal revalidates the subject list before
            // it sends (m_pchsCurrent->Reset(), console.cpp:1453): a group member
            // that left the mission drops out and a one-ship group becomes an
            // individual.
            ResetChatSubjectBeforeSend(world);
            short me = world.MyShipId ?? -1;
            selfId = me;
            Allegiance.ClientCore.State.ShipEntity? ownShip = null;
            if (me >= 0) world.Ships.TryGetValue(me, out ownShip);
            switch (_chatTarget)
            {
                case ChatTarget.Individual:
                    // P818 — the picked ship (native m_oidRecipient,
                    // console.cpp:300-302), the player's own only when the "Me"
                    // column (or a self pick) chose it.
                    oidRecipient = _chatRecipient.IndividualShipId ?? (me >= 0 ? me : (short)0);
                    break;
                case ChatTarget.Wing: if (ownShip is { } wingOwn) oidRecipient = wingOwn.WingId; break;
                case ChatTarget.Team: if (ownShip is { } sideOwn) oidRecipient = sideOwn.SideId; break;
                case ChatTarget.FriendlySector:
                case ChatTarget.AllSector:
                    // P787b — SIM-0332: the compose's sector recipient is the
                    // last RIGHT-CLICK-picked cluster when set (native
                    // console.cpp:1745-1746 SetRecipient), else the chat
                    // cluster fallback (native converts NA → GetChatCluster(),
                    // clintlib.cpp:2352-2366: the viewed cluster, else the own).
                    oidRecipient = _chatSectorRecipient
                        ?? world.ViewedClusterId
                        ?? (ownShip is { } sectorOwn ? sectorOwn.SectorId : (short)0);
                    break;
            }
            // The wire plan is native's send loop, built with or without an own
            // ship: a group recipient still fans out per subject, and an
            // Individual still addresses the picked ship (a bare transport with
            // no world simply resolves nothing, like the pre-P818 send).
            plan = Rendering.ChatSendPlan.Plan(_chatTarget, _chatRecipient.Ships, oidRecipient, selfId);
        }
        // No ship list and no individual (a world still settling): one chat for
        // the channel target, exactly as before P818.
        if (plan.Count == 0)
            plan = [new Rendering.ChatSendPlan.Send(_chatTarget, oidRecipient, LocalOnly: false, EchoLocally: false)];
        // P818 — the send loop (native console.cpp:1448-1507): a Group recipient
        // goes out as ONE message per subject (first CHAT_GROUP, the rest
        // CHAT_GROUP_NOECHO — the server rewrites each to CHAT_INDIVIDUAL on its
        // oidRecipient, FedSrv.CPP:3531-3536); an Individual to yourself is never
        // put on the wire (clintlib.cpp:2422-2426). The sender's chat history
        // gains exactly one copy: the server's relay when the line is addressed
        // to the player's own ship, otherwise the local echo that native's
        // ReceiveChat performs for every send except CHAT_GROUP_NOECHO.
        foreach (var send in plan)
        {
            if (!send.LocalOnly)
                _session.SendChat(text, send.Target, oidRecipient: send.OidRecipient,
                    commandId: OrderChange.CidNone);
            if (send.EchoLocally)
                lock (_session.Gate)
                    _session.World.PostOwnChatLine(text, send.Target, send.OidRecipient);
            // P880a — GetChatCondition production binding: a real player chat send routes to the
            // active mission's RecordChat (native trainingmission.cpp:327-331 RecordChat), so the
            // M4 "chat with the squad" goal observes the player's own chat instead of a host-injected
            // one. Only the primary subject counts once per send loop.
            _trainingSession?.Mission.RecordChat((int)send.Target);
        }
    }

    /// <summary>MonoGame's real text-input handler (<c>Window.TextInput</c>):
    /// appends printable characters to the active composer — the in-flight chat
    /// line, or (P274) the lobby filter box while <see cref="_filterActive"/>.
    /// Control characters (Enter/Backspace/Escape) are handled by the key-edge
    /// logic in <see cref="UpdateFlying"/> / <see cref="UpdateLobby"/>, so they
    /// are filtered here to avoid double handling.</summary>
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        char c = e.Character;
        if (c == '\r' || c == '\n' || c == '\b' || c == (char)27) // Enter / Back / Esc
            return;
        if (c < 32 || c == 127)
            return;

        // P853b — the team lobby's TEAM NAME edit box owns typed text while it is
        // open (native's teamNameEditPane, teamscreen.cpp:887/897: max length
        // c_cbName - 1 = 24). Same Window.TextInput path as the chat line and the
        // lobby filter, so editing feels identical and needs no new input plumbing.
        if (_state == ClientState.ReadyCheck && _teamNameEditing)
        {
            if (_teamNameBuffer.Length < State.TeamNameEdit.MaxLength)
                _teamNameBuffer.Append(c);
            return;
        }

        if (_state == ClientState.Lobby && _filterActive)
        {
            if (_filterBuffer.Length < MaxFilterLen)
            {
                _filterBuffer.Append(c);
                RebuildVisibleRows();   // narrow the visible list live as you type
            }
            return;
        }

        // P761 — the find-player popup's edit box (native FindDialogPopup: the
        // edit pane owns typed text until Enter submits the search).
        if (_state == ClientState.Lobby && _findPlayerActive)
        {
            if (_findBuffer.Length < MaxFilterLen)
                _findBuffer.Append(c);
            return;
        }

        if (_state == ClientState.Intro)
        {
            if (_introView == IntroView.Main && _introEditingPilot && _pilotBuf.Length < 24)
                _pilotBuf.Append(c);
            else if (_introView == IntroView.DirectConnect && _introEditingDirect && _directBuf.Length < 128)
                _directBuf.Append(c);
            else if (_introView == IntroView.CreateGame)
                _createGame.TypeName(c.ToString());   // P871 — game-name edit box
            return;
        }

        if (_state == ClientState.Flying && _chatActive)
        {
            // P784 — native OnChar's special characters (console.cpp:2386-2396):
            // '`'/'~' quit composing and open the main quick-chat menu; '!' —
            // while composing a plain CHAT with an EMPTY buffer — switches the
            // compose to the SHELL state (the '!' itself stays in the buffer,
            // console.cpp:2388-2396).
            if (c is '`' or '~')
            {
                SetComposeState(NativeComposeState.NotComposing);   // QuitComposing
                OpenQuickChat();                                    // OpenMainQuickChatMenu
                return;
            }
            if (c == '!' && LiveComposeState == NativeComposeState.Chat && _chatBuffer.Length == 0)
                SetComposeState(NativeComposeState.Shell);
            if (_chatBuffer.Length < MaxChatLen)
            {
                _chatBuffer.Append(c);
                // SIM-0318 — native OnChar plays chatKeySound for every
                // printable key while composing past the mouse-command state
                // (console.cpp:2337; sounddef.mdl:324 text wave).
                if (ChatKeySound.OnChar(LiveComposeState))
                    _sound?.PlayChatKey();
            }
            return;
        }

        // P761 — the character sheet's profile editor draft (native's edit pane
        // owns typed text until Apply).
        if (_state == ClientState.Flying && _pilotProfile.Editing)
        {
            _pilotProfile.Append(c);
            return;
        }
    }

    /// <summary>Build the display strings for the in-flight chat log from the
    /// real, already-populated <c>ClientWorld.Events.Chats</c> (arrival-ordered),
    /// resolving each sender's character name from the world roster where known.
    /// P626 — respects the current chat-pane scroll offset so scrolled-
    /// back history renders instead of always the newest page. P757 — the lines
    /// are first passed through the pane's channel filter (native
    /// <c>ChatPassesFilter</c>, chatpane.cpp:299-321): the player-chats-only
    /// flag and any selected channels drop messages before the window is
    /// taken, exactly like native's filtered <c>ChatListPaneImpl</c>.</summary>
    private List<string> BuildChatLines() => BuildChatLinesAndColors().Lines;

    /// <summary>HUD-10 — the same visible chat window plus each line's native
    /// colour. Native <c>ChatInfo::SetChat</c> stores the sender's side colour
    /// scaled by 0.85 (or 0.75 for your own non-echo line) and the pane draws
    /// that colour (trekigc.cpp:4736-4741, chatpane.cpp:93-101). Unknown senders
    /// stay null so the renderer falls back to its neutral text colour.
    /// P784 — each line also carries native's <c>IsFromLeader</c> flag
    /// (trekigc.cpp:4498: the sender's <c>PlayerInfo::IsTeamLeader()</c>),
    /// resolved from the port's per-side leader roster
    /// (<c>TeamLeaderShipIdFor</c>); the pane draws those lines bold
    /// (chatpane.cpp:34,93).</summary>
    private (List<string> Lines, List<Rendering.HudColor?> Colors, List<bool> Leaders) BuildChatLinesAndColors()
    {
        var world = _session.World;
        lock (_session.Gate)
        {
            var chats = world.Events.Chats;
            var visible = new List<ChatMessage>(Math.Min(chats.Count, ChatRenderer.MaxVisible));
            foreach (var m in chats)
                if (ChatLinePassesPaneFilter(world, m))
                    visible.Add(m);
            int start = ChatScroll.StartIndex(_chatPane.Offset, visible.Count, ChatRenderer.MaxVisible);
            var lines = new List<string>(Math.Min(visible.Count, ChatRenderer.MaxVisible));
            var colors = new List<Rendering.HudColor?>(Math.Min(visible.Count, ChatRenderer.MaxVisible));
            var leaders = new List<bool>(Math.Min(visible.Count, ChatRenderer.MaxVisible));
            int end = Math.Min(visible.Count, start + ChatRenderer.MaxVisible);
            for (int i = start; i < end; i++)
            {
                var m = visible[i];
                string sender = "";
                Allegiance.ClientCore.State.ShipEntity? ent = null;
                if (m.SidSender >= 0 && world.Ships.TryGetValue(m.SidSender, out var found) && !string.IsNullOrEmpty(found.CharacterName))
                {
                    sender = found.CharacterName!;
                    ent = found;
                }
                else if (m.SidSender >= 0)
                    sender = $"sid{m.SidSender}";
                lines.Add(ChatLogFormat.Format(sender, m.Message, (ChatTarget)m.ChatTarget));

                // P784 — native stamps IsFromLeader from the sender's
                // PlayerInfo::IsTeamLeader() (trekigc.cpp:4498); the port reads
                // the same fact from its per-side leader roster. A leaderless
                // side (no SET_TEAM_LEADER yet) marks nobody, like native's
                // false default (trekigc.cpp:4484).
                bool fromLeader = ent is { } leaderEnt
                    && world.TeamLeaderShipIdFor(leaderEnt.SideId) is { } leaderId
                    && leaderId == leaderEnt.ShipId;
                leaders.Add(fromLeader);

                Rendering.HudColor? color = null;
                if (ent is { } e && world.TryGetSideColor(e.SideId, out float r, out float g, out float b))
                {
                    bool own = world.MyShipId is { } me && e.ShipId == me;
                    float scale = own ? 0.75f : 0.85f;
                    color = new Rendering.HudColor(r * scale, g * scale, b * scale, 1f);
                }
                colors.Add(color);
            }
            // P871 — the directory-wide lobby channel shares this log, so it
            // renders identically in Classic and Modern (both draw BuildChatLines).
            // (A headless test instance has no directory-chat log.)
            // P871b — a directory line is never cut by MaxVisible: P871 stopped
            // appending once the GAME lines filled the window (`break`), so a
            // directory line that arrived after a full chat log was silently
            // dropped from the pane. The window is now applied to the MERGED list
            // and trims the OLDEST overflow, so both sources' newest lines show.
            foreach (var line in _directoryChat?.Lines ?? [])
            {
                lines.Add(DirectoryChatLog.Format(line));
                colors.Add(null);
                leaders.Add(false);
            }
            int overflow = lines.Count - ChatRenderer.MaxVisible;
            if (overflow > 0)
            {
                lines.RemoveRange(0, overflow);
                colors.RemoveRange(0, overflow);
                leaders.RemoveRange(0, overflow);
            }
            return (lines, colors, leaders);
        }
    }

    /// <summary>P757 — does one world chat line pass the pane's channel filter?
    /// Native <c>ChatInfo::IsFromPlayer</c> is "sender ship exists and its
    /// PlayerInfo is human" (trekigc.cpp:4800-4802, clintlib.cpp:624); the
    /// port's world only names player ships (EXPORT_SHIP carries the character
    /// name, Server.Broadcast.cs), so "from player" ≡ the sender resolves to a
    /// non-empty character name. <c>BObjectModel</c> is the wire's own object-
    /// model flag. Called under the session gate.</summary>
    private bool ChatLinePassesPaneFilter(Allegiance.ClientCore.State.ClientWorld world, ChatMessage m)
    {
        bool fromPlayer = m.SidSender >= 0
            && world.Ships.TryGetValue(m.SidSender, out var ent)
            && !string.IsNullOrEmpty(ent.CharacterName);
        // P806 — native m_bFilterChatsToAll: a message to CHAT_EVERYONE while in
        // game is dropped before it reaches the pane (ReceiveChat,
        // trekigc.cpp:4493; toggle = idmFilterChatsToAll, WinTrek.cpp:4572).
        if (_chatFilterChatsToAll && (ChatTarget)m.ChatTarget == ChatTarget.Everyone
            && world.MyShipId is not null)
            return false;
        return _chatPane.PassesFilter(fromPlayer, m.BObjectModel, (ChatTarget)m.ChatTarget);
    }

    /// <summary>P757 — count of chat lines the pane's filter currently admits
    /// (the window/offset math runs over the filtered list, like native's
    /// filtered <c>ChatListPaneImpl</c>).</summary>
    private int FilteredChatCount()
    {
        var world = _session.World;
        lock (_session.Gate)
        {
            int n = 0;
            foreach (var m in world.Events.Chats)
                if (ChatLinePassesPaneFilter(world, m))
                    n++;
            return n;
        }
    }

    /// <summary>P806 — apply the pane's TWO native filters (see the field remarks
    /// on <c>_chatPlayerChatsOnly</c>/<c>_lobbyChatTarget</c>):
    /// <list type="bullet">
    /// <item><c>m_bPlayerChatsOnly</c> (the Game Options chat filter,
    /// chatpane.cpp:146,228-233) — always applied;</item>
    /// <item>the LOBBY channel set, while the team lobby (ReadyCheck) is up:
    /// native <c>OnButtonBarChat</c> resets the list and adds the selected target
    /// plus Individual/IndividualNoFilter/Admin (teamscreen.cpp:3088-3112), with
    /// the Leaders button gated on team leadership (:3104-3116). In flight the
    /// list stays empty (native's console chat pane selects no channels,
    /// console.cpp:3439-3442).</item>
    /// </list>
    /// Every mutator rebuilds the view and snaps it to the live edge with
    /// autoscroll on (<c>UpdateContents</c>, chatpane.cpp:286-297, 340-358).</summary>
    private void ApplyChatPaneFilter()
    {
        _chatPane.SetPlayerChatsOnly(_chatPlayerChatsOnly);
        _chatPane.ResetChannels();
        if (_state == ClientState.ReadyCheck)
            _chatPane.ApplyLobbyChatTarget(_lobbyChatTarget, IsTeamLobbyLeader());
        _lastChatCount = FilteredChatCount();
    }

    /// <summary>P806 — is the local player its side's team leader (the gate on
    /// native's Leaders chat button, teamscreen.cpp:3104)?</summary>
    private bool IsTeamLobbyLeader()
    {
        lock (_session.Gate)
        {
            var world = _session.World;
            return world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var own)
                && world.TeamLeaderShipIdFor(own.SideId) == me;
        }
    }

    /// <summary>P806 — short label of the chat pane's filter state, drawn in the
    /// pane's header: the native players-only switch plus the lobby channel when
    /// the team lobby set one.</summary>
    private string ChatPaneFilterLabel()
    {
        string players = _chatPlayerChatsOnly ? "PLAYERS" : "ALL";
        if (_state == ClientState.ReadyCheck)
            return $"{players} / {_lobbyChatTarget.ToString().ToUpperInvariant()}";
        return players;
    }

    /// <summary>Render the in-flight chat overlay (log panel + active composing
    /// line) on top of the HUD.</summary>
    private void DrawChat(Rendering.HudModel model)
    {
        _chatRenderer.Draw(model, GraphicsDevice.Viewport);
    }

    /// <summary>Open the quick-chat menu at its root (<c>mainMenu</c>). No-op if
    /// the data failed to load.</summary>
    private void OpenQuickChat()
    {
        if (_quickChatDb?.MainMenu is not { } root)
            return;
        _qcMenuOpen = true;
        _qcMenuPath = new List<QuickChatMenu> { root };
        _qcMenuNode = root;
        // Prime the rising-edge set so the R that just opened the menu is not
        // also consumed as a selection on this same frame.
        _qcKeysPrev.Clear();
        foreach (var c in root.Choices)
            if (CharToXnaKey(c.Key) is { } k)
                _qcKeysPrev.Add(k);
        Console.WriteLine("[monogame] quick-chat menu (R closes via ESC; select by shortcut key)");
    }

    /// <summary>Handle one frame of quick-chat menu navigation: shortcut keys
    /// descend into submenus or send leaf commands (rising-edge detected via
    /// <see cref="_qcKeysPrev"/>). Escape is handled in <see cref="Update"/>.</summary>
    private void NavigateQuickChat(KeyboardState ks)
    {
        if (_qcMenuNode is not { } node)
            return;

        foreach (var choice in node.Choices)
        {
            if (CharToXnaKey(choice.Key) is not { } key)
                continue;
            bool down = ks.IsKeyDown(key);
            if (down && !_qcKeysPrev.Contains(key))
            {
                if (choice.Submenu is { } sub)
                {
                    DescendQuickChat(sub);
                }
                else if (choice.Send is { } sa)
                {
                    SendQuickChatLeaf(sa);
                }
                break;   // one selection per frame
            }
        }

        // Remember this frame's down-state for next frame's rising-edge test,
        // computed against the (possibly descended) current menu.
        _qcKeysPrev.Clear();
        if (_qcMenuNode is { } cur)
            foreach (var c in cur.Choices)
                if (CharToXnaKey(c.Key) is { } k && ks.IsKeyDown(k))
                    _qcKeysPrev.Add(k);
    }

    /// <summary>Send a leaf quick-chat command: a real <c>CHATMESSAGE</c> whose
    /// <c>voiceOver</c> is the command's sonic-chat index, with native null text
    /// (the receiver derives the spoken caption), and — P457 —
    /// whose <c>CommandID</c> + <c>OidTarget</c>/<c>OtTarget</c> carry the
    /// command's resolved order and target (native <c>SendQuickChat</c> →
    /// <c>SendChatInternal</c>, console.cpp:1592-1596). A voice-only line (no
    /// resolvable target / <c>cidNone</c>) sends <c>CommandID = c_cidNone</c>,
    /// exactly as today. The voice is NOT played locally here — the server relays
    /// the sender's own echo back into <see cref="Events.Chats"/>, and
    /// the ClientWorld sound-event path voices that single copy.
    /// Closes the menu.</summary>
    private void SendQuickChatLeaf(QuickChatSendAction send)
    {
        CloseQuickChat();
        var order = ResolveQuickChatOrder(send);
        _session.SendChat("", send.Target,
            voiceOver: (short)send.Entry.Index,
            commandId: order.CommandId,
            oidTarget: order.OidTarget,
            otTarget: (short)order.OtTarget,
            absentMessage: true); // console.cpp:1593 sends NULL text + sonic index
        Console.WriteLine($"[monogame] quick-chat >> #{send.Entry.Index} \"{send.Entry.Caption}\" ({send.Target}) " +
                          $"cmd={(order.CarriesOrder ? order.CommandId : (short)OrderChange.CidNone)} target=({order.OtTarget}, {order.OidTarget})");
    }

    /// <summary>P457 — resolve the leaf command's order target against the live
    /// client state (own ship/side/position, T-selected target, P424 queued order,
    /// same-sector candidates) and produce the wire <c>CommandID</c> + target.</summary>
    private ResolvedQuickChatOrder ResolveQuickChatOrder(QuickChatSendAction send)
    {
        var world = _session.World;
        if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own) || own.Latest is not { } up)
            return new ResolvedQuickChatOrder(OrderChange.CidNone, (IgcObjectType)(-1), -1);
        List<CandidateObject> cands;
        lock (_session.Gate)
            cands = GatherSectorCandidates(world.Ships, world.Warps, world.Stations, world.Asteroids,
                                           me, own.SectorId, ShipRoleBits, StationRoleBits);
        return QuickChatOrderResolver.Resolve(
            send.CommandId, send.TargetTypeMask, send.AbilityMask,
            me, world.MySideId, up.Position,
            _autoTarget, world.MyQueuedCommand, cands);
    }

    private void DescendQuickChat(QuickChatMenu sub)
    {
        _qcMenuPath.Add(sub);
        _qcMenuNode = sub;
    }

    /// <summary>Back out one menu level, or close the menu entirely when already
    /// at the top — never quits the game (same care P214 took with chat's
    /// Escape).</summary>
    private void BackQuickChat()
    {
        if (_qcMenuPath.Count <= 1)
            CloseQuickChat();
        else
        {
            _qcMenuPath.RemoveAt(_qcMenuPath.Count - 1);
            _qcMenuNode = _qcMenuPath[^1];
        }
    }

    private void CloseQuickChat()
    {
        _qcMenuOpen = false;
        _qcMenuNode = null;
        _qcMenuPath.Clear();
        _qcKeysPrev.Clear();
    }

    /// <summary>Map a <c>QuickChatMenuItem.char</c> shortcut (a digit or ASCII
    /// letter, as in the real data) to a MonoGame <c>Keys</c> value. Returns
    /// null for anything unusual (not a digit/letter).</summary>
    private static Keys? CharToXnaKey(char c)
    {
        if (c >= '0' && c <= '9') return Keys.D0 + (c - '0');
        if (c >= 'A' && c <= 'Z') return Keys.A + (c - 'A');
        if (c >= 'a' && c <= 'z') return Keys.A + (c - 'a');   // physical key, not shifted
        return null;
    }

    /// <summary>P813c — the experimental reconnect-grace window (seconds) from
    /// <c>ALLEGIANCE_RECONNECT_GRACE_SECONDS</c>; null (the default) = off, i.e.
    /// the pre-P813c behaviour where a dropped session shows the disconnect
    /// screen and stays there.</summary>
    private static double? ReconnectGraceSecondsFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("ALLEGIANCE_RECONNECT_GRACE_SECONDS");
        return double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? seconds
            : null;
    }

    /// <summary>P894 — the client build this process reports at logon (the
    /// installed release the updater wrote, "0.0.0" for a hand-installed tree).
    /// The game server compares it with its advertised minimum client version and
    /// refuses an older build — native's version gate, moved to the logon the port
    /// actually performs (<c>lobby/client.cpp:272-309</c>). Resolved once: the
    /// on-disk manifest only changes across a restart (the update applies on
    /// restart).</summary>
    internal static readonly string InstalledClientVersion =
        Allegiance.Update.UpdateState.ResolveInstalledVersion(AppContext.BaseDirectory);

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name,
        ZoneTicket = null,
        CDKey = "ABCDEFGHIJKL",
        MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer,
        Time = 1234u,
        DwCookie = uint.MaxValue,
        // P871 — report the installed file set (native FileCRC("FileList.txt"),
        // clintlib.cpp:1418); 0 when nothing is installed (checks disabled).
        CrcFileList = ClientFileList.Compute(),
        // P894 — the P894 client-version extension (the advertised minClientVersion
        // gate); absent bytes on the wire until this is set.
        ClientVersion = InstalledClientVersion,
        SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0,
        SteamID = 70000000000000000ul,
        DrmHash = "monogame",
    };
}
