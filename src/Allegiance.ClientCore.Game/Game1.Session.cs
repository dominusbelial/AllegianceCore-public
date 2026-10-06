using System.Net.Http;
using System.Net.Http.Json;
using System.Linq;
using System.Text;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
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
        public string PlayerName = "monogame";

        /// <summary>Non-null selects DIRECT connect (skip the lobby), connecting
        /// straight to <see cref="DirectPort"/> on loopback — the unchanged
        /// <c>[host] [port] [name]</c> dev/testing shortcut.</summary>
        public string? DirectHost;

        public ushort DirectPort;

        /// <summary>Base URL of the lobby directory whose <c>/api/servers</c>
        /// the server-browser state polls. Default documented in
        /// <see cref="ServerListing.LobbyUrlDefault"/>.</summary>
        public string LobbyUrl = ServerListing.LobbyUrlDefault;

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
        public float DebrisDensity = 1.0f;

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

        /// <summary>P500 — the soft-keyboard text-entry seam for the connect
        /// screen. Null defaults to MonoGame's platform <see cref="KeyboardInput"/>.</summary>
        public ITextPrompt? TextPrompt;
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
    private enum ClientState { Lobby, Connecting, SideSelect, ReadyCheck, Flying, Disconnected }

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
                _servers.Clear();
                _servers.AddRange(data);
                RebuildDisplay();   // P500 — merge saved servers + fetched listings
            }
            _lobbyResultData = null;
            _lobbyResultError = null;
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

        // Navigation: arrows (also free pre-flight — no flight keys collide).
        // _selectedIndex is an index INTO the filtered _visibleRows, so with no
        // filter active it behaves exactly as before (every row visible).
        bool up = ks.IsKeyDown(XnaKeys.Up);
        if (up && !_upWasDown && _visibleRows.Count > 0)
            _selectedIndex = (_selectedIndex - 1 + _visibleRows.Count) % _visibleRows.Count;
        _upWasDown = up;

        bool down = ks.IsKeyDown(XnaKeys.Down);
        if (down && !_downWasDown && _visibleRows.Count > 0)
            _selectedIndex = (_selectedIndex + 1) % _visibleRows.Count;
        _downWasDown = down;

        // Manual retry: R forces an immediate refetch (helps an unreachable lobby).
        bool rRetry = ks.IsKeyDown(XnaKeys.R);
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
        bool fKey = ks.IsKeyDown(XnaKeys.F);
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
        bool enter = ks.IsKeyDown(XnaKeys.Enter);
        if (enter && !_enterWasDown)
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

    /// <summary>Rebuild the <see cref="_visibleRows"/> index list from
    /// <see cref="_displayServers"/> (saved servers + fetched lobby listings)
    /// under the current filter text, then re-clamp the selection into it.
    /// Called whenever the combined list or the filter text changes (fetch
    /// result, F toggle, typing via OnTextInput, Backspace, connect edits).
    /// Always leaves <see cref="_selectedIndex"/> in range.</summary>
    private void RebuildVisibleRows()
    {
        string needle = _filterActive ? _filterBuffer.ToString() : string.Empty;
        _visibleRows.Clear();
        for (int i = 0; i < _displayServers.Count; i++)
            if (_displayServers[i].Matches(needle))
                _visibleRows.Add(i);
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

    /// <summary>HTTP GET the lobby's <c>/api/servers</c> and stage the result for
    /// the main thread (see <see cref="UpdateLobby"/>). Standard
    /// <see cref="HttpClient"/> — no MonoGame networking involved. Never throws;
    /// failures become an on-screen error and the cadence just retries.</summary>
    private async Task FetchServersAsync()
    {
        List<ServerListing>? data = null;
        string? error = null;
        try
        {
            data = await _http.GetFromJsonAsync<List<ServerListing>>(
                "/api/servers", ServerListing.JsonOptions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = $"Lobby unreachable ({_lobbyUrl}): {ex.Message} - retrying...";
        }

        // Volatile write AFTER filling the data so the main thread's volatile
        // read of _lobbyResultPending pairs correctly.
        _lobbyResultData = data;
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

        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            var titleCol = new Color(HudColor.TealAccent.R, HudColor.TealAccent.G, HudColor.TealAccent.B);
            var dimCol = new Color(0.48f, 0.64f, 0.62f);
            var textCol = new Color(0.88f, 0.96f, 0.95f);
            var savedCol = new Color(0.70f, 0.95f, 0.75f);

            var title = "SERVER BROWSER";
            var tsz = _hudBig.MeasureString(title);
            _spriteBatch.DrawString(_hudBig, title, new Vector2((w - tsz.X) / 2f, 28f), titleCol);

            string sub = polling ? $"Polling {_lobbyUrl}/api/servers" : "Manual connect (no lobby)";
            var ssz = _hudSmall.MeasureString(sub);
            _spriteBatch.DrawString(_hudSmall, sub, new Vector2((w - ssz.X) / 2f, 82f), dimCol);

            if (connect)
            {
                string nameLine = $"Player: {_playerName}   (tap NAME to change)";
                _spriteBatch.DrawString(_hudSmall, nameLine, new Vector2(60f, 112f), textCol);
            }

            // Banner (error / empty).
            if (_lobbyError is { } err)
            {
                _spriteBatch.DrawString(_hudSmall, err, new Vector2(60f, ConnectScreenLayout.ListTop - 26f),
                    new Color(1f, 0.45f, 0.35f));
                if (polling)
                    _spriteBatch.DrawString(_hudSmall, "Press R to retry now.", new Vector2(60f, ConnectScreenLayout.ListTop - 2f), dimCol);
            }
            else if (connect && _savedServers.Count == 0 && !polling)
            {
                _spriteBatch.DrawString(_hudSmall, "No saved servers yet - tap ADD to create one.",
                    new Vector2(60f, ConnectScreenLayout.ListTop - 26f), dimCol);
            }
            else if (polling && _displayServers.Count == 0)
            {
                _spriteBatch.DrawString(_hudSmall, "No servers found yet - waiting for registrations...",
                    new Vector2(60f, ConnectScreenLayout.ListTop - 26f), dimCol);
            }
            else if (_filterActive && _visibleRows.Count == 0)
            {
                _spriteBatch.DrawString(_hudSmall, $"No servers match '{_filterBuffer}'.",
                    new Vector2(60f, ConnectScreenLayout.ListTop - 26f), new Color(1f, 0.6f, 0.4f));
            }

            // P274 — the live filter/edit line. Drawn above the list so the
            // narrowed selection is read against it. '_' is a blinking cursor
            // stand-in (fonts bake ASCII 32..126 only, no box-drawing glyphs).
            if (_filterActive)
            {
                string prompt = "Filter: " + _filterBuffer.ToString() + "_";
                var psz = _hudSmall.MeasureString(prompt);
                _spriteBatch.DrawString(_hudSmall, prompt, new Vector2(60f, ConnectScreenLayout.ListTop - 52f),
                    new Color(0.98f, 1.0f, 0.65f));
                string cnt = $"{_visibleRows.Count}/{_displayServers.Count}";
                _spriteBatch.DrawString(_hudSmall, cnt, new Vector2(60f + psz.X + 24f, ConnectScreenLayout.ListTop - 49f), dimCol);
            }

            // Combined rows (saved servers first, then lobby listings).
            for (int i = 0; i < _visibleRows.Count; i++)
            {
                int di = _visibleRows[i];
                var s = _displayServers[di];
                float y = ConnectScreenLayout.ListTop + i * ConnectScreenLayout.RowH;
                bool sel = i == _selectedIndex;
                bool isSaved = di < _savedServers.Count;
                _spriteBatch.Draw(_whitePixel, new Rectangle(50, (int)y, (int)(w * 0.7f), (int)(ConnectScreenLayout.RowH - 6f)),
                    sel ? new Color(0.08f, 0.35f, 0.33f, 0.55f) : new Color(0f, 0f, 0f, 0f));

                string prefix = isSaved ? "[S] " : "";
                string suffix = "";
                if (isSaved && _lastUsedKey is { } lk && _savedServers[di].HostPort == lk)
                    suffix = "  (last used)";
                string line = prefix + RowText(s) + suffix;
                _spriteBatch.DrawString(_hudSmall, line, new Vector2(56f, y + 2f),
                    sel ? titleCol : (isSaved ? savedCol : textCol));
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
                    : "UP/DOWN select   ENTER join   F find   R refresh   ESC quit");
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
    private void BeginConnect(string host, ushort port)
    {
        _state = ClientState.Connecting;
        // P500 — record the ACTIVE target here, not just from LaunchOptions.
        // Every join (lobby, connect screen, resume) remembers where we are, so
        // the P499 resume-reconnect (NotifyResumed) and the connect screen's
        // last-used memory both work regardless of entry path.
        _connectHost = host;
        _connectPort = port;
        if (_connectStore is not null && !string.IsNullOrEmpty(host))
        {
            _lastUsedKey = $"{host}:{port}";
            PersistConnectProfile();
        }
        Console.WriteLine($"[monogame] connecting to {host}:{port} as '{_playerName}' ...");
        try
        {
            _session.Connect(port, _playerName, host);
            _session.SendLogonRequest(Request(_playerName));
            var ack = _session.WaitForAck(5000);
            Console.WriteLine($"[monogame] logged on: serverTime={ack.TimeServer}");
            // P281 — the server now defers our spawn to a post-connect side-select
            // step: after LOGONACK it pushes a SIDE_ROSTER(211) snapshot and holds
            // the peer (no ship, no SET_CLUSTER) until we send FM_C_POSITIONREQ(39).
            // Enter the side-select screen here; UpdateSideSelect sends POSITIONREQ
            // and transitions to Flying once the server seats us (SET_CLUSTER sets
            // World.MyShipId).
            _state = ClientState.SideSelect;
            _sideSelectIndex = 0;
            _sideSelectFromGameOver = false;   // P481 — a fresh logon is never an eliminated-side lobby
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] FAILED to connect/log on: {ex.Message}");
            if (_returnToLobbyOnFailure)
            {
                _returnToLobbyOnFailure = false;
                _state = ClientState.Lobby;
                _lobbyError = $"Could not join {host}:{port} - {ex.Message}";
            }
            else
            {
                Exit();
            }
        }
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
            try { BeginConnect(_connectHost, _connectPort); }
            finally { _returnToLobbyOnFailure = prior; }
        }
    }

    // ── P214 — chat ──────────────────────────────────────────────────────────
    /// <summary>Sends the composed line to the current <see cref="_chatTarget"/>
    /// (default Team, P456) and closes the input.</summary>
    private void SendChatLine()
    {
        string text = _chatBuffer.ToString().Trim();
        _chatActive = false;
        _chatBuffer.Clear();
        if (text.Length == 0)
            return;
        Console.WriteLine($"[monogame] chat ({_chatTarget}) >> {text}");
        // P456: send to the compose's current target, not a hardcoded Everyone —
        // the recipient is client state (_chatTarget) so Tab cycling works and
        // the target persists across compose sessions. The server relays the
        // sender's own echo back, which is what actually displays the line (it
        // is NOT echoed locally). commandId=c_cidNone marks a typed chat as
        // carrying no order (P457), so the server never queues one from it.
        _session.SendChat(text, _chatTarget, commandId: OrderChange.CidNone);
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

        if (_state == ClientState.Lobby && _filterActive)
        {
            if (_filterBuffer.Length < MaxFilterLen)
            {
                _filterBuffer.Append(c);
                RebuildVisibleRows();   // narrow the visible list live as you type
            }
            return;
        }

        if (_state == ClientState.Flying && _chatActive)
        {
            if (_chatBuffer.Length < MaxChatLen)
                _chatBuffer.Append(c);
        }
    }

    /// <summary>Build the display strings for the in-flight chat log from the
    /// real, already-populated <c>ClientWorld.Events.Chats</c> (arrival-ordered),
    /// resolving each sender's character name from the world roster where known.</summary>
    private List<string> BuildChatLines()
    {
        var world = _session.World;
        lock (_session.Gate)
        {
            var chats = world.Events.Chats;
            var lines = new List<string>(Math.Min(chats.Count, ChatRenderer.MaxVisible));
            int start = Math.Max(0, chats.Count - ChatRenderer.MaxVisible);
            for (int i = start; i < chats.Count; i++)
            {
                var m = chats[i];
                string sender = "";
                if (m.SidSender >= 0 && world.Ships.TryGetValue(m.SidSender, out var ent) && !string.IsNullOrEmpty(ent.CharacterName))
                    sender = ent.CharacterName!;
                else if (m.SidSender >= 0)
                    sender = $"sid{m.SidSender}";
                lines.Add(ChatLogFormat.Format(sender, m.Message, (ChatTarget)m.ChatTarget));
            }
            return lines;
        }
    }

    /// <summary>Render the in-flight chat overlay (log panel + active composing
    /// line) on top of the HUD.</summary>
    private void DrawChat()
    {
        _chatRenderer.Draw(
            BuildChatLines(),
            _chatActive ? _chatBuffer.ToString() : string.Empty,
            _chatActive,
            _nowSeconds,
            GraphicsDevice.Viewport,
            ChatTargetLogic.ComposeLabel(_chatTarget));
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
    /// <c>voiceOver</c> is the command's sonic-chat index, whose text is the
    /// spoken caption (the same text the receiving side displays), and — P457 —
    /// whose <c>CommandID</c> + <c>OidTarget</c>/<c>OtTarget</c> carry the
    /// command's resolved order and target (native <c>SendQuickChat</c> →
    /// <c>SendChatInternal</c>, console.cpp:1592-1596). A voice-only line (no
    /// resolvable target / <c>cidNone</c>) sends <c>CommandID = c_cidNone</c>,
    /// exactly as today. The voice is NOT played locally here — the server relays
    /// the sender's own echo back into <see cref="Events.Chats"/>, and
    /// <see cref="ProcessIncomingQuickChatVoices"/> voices that single copy.
    /// Closes the menu.</summary>
    private void SendQuickChatLeaf(QuickChatSendAction send)
    {
        CloseQuickChat();
        var order = ResolveQuickChatOrder(send);
        _session.SendChat(send.Entry.Caption, send.Target,
            voiceOver: (short)send.Entry.Index,
            commandId: order.CommandId,
            oidTarget: order.OidTarget,
            otTarget: (short)order.OtTarget);
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

    /// <summary>
    /// P217 receive path — play the real voice for each newly-arrived quick-chat.
    /// Walks <see cref="Events.Chats"/> forward from the last processed message
    /// (a watermark reference; if that message has been evicted from the bounded
    /// 500-entry log, it re-anchors on the newest to avoid a re-play burst).
    /// A chat is voiced only when its <c>voiceOver</c> index resolves to a real
    /// entry whose caption matches the message text — which is what
    /// distinguishes an actual quick-chat (index + spoken caption) from a typed
    /// chat (index 0, arbitrary text that never equals a caption). This same
    /// path voices the sender's own server-echoed macro exactly once.
    /// </summary>
    private void ProcessIncomingQuickChatVoices()
    {
        if (_quickChatDb is null || _sound is null)
            return;
        var chats = _session.World.Events.Chats;
        if (chats.Count == 0)
            return;

        // P319 (finding H-2) — the watermark scan + forward walk index into the
        // same bounded 500-entry list the receive thread appends/evicts under
        // _session.Gate (EventLog.Record -> Append, driven from OnReceive).
        // Enumerating it unlocked during busy chat traffic threw
        // InvalidOperationException ("Collection was modified") on the render
        // thread, or could skip/duplicate a voice. Following the P306
        // ReadyCheckRoster.Gather precedent: snapshot the new arrivals and
        // advance the watermark under the lock, then play voices outside it
        // (playback touches only _sound / the static core db, never the shared
        // list). _lastVoiceProcessed is render-thread-only, so advancing it
        // inside the lock is safe and keeps behavior identical to before.
        var toVoice = new List<ChatMessage>();
        lock (_session.Gate)
        {
            int start = 0;
            if (_lastVoiceProcessed is not null)
            {
                bool found = false;
                for (int i = 0; i < chats.Count; i++)
                {
                    if (ReferenceEquals(chats[i], _lastVoiceProcessed))
                    {
                        start = i + 1;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    // Evicted from the window: anchor on the newest only.
                    _lastVoiceProcessed = chats[^1];
                    return;
                }
            }

            for (int i = start; i < chats.Count; i++)
                toVoice.Add(chats[i]);
            if (toVoice.Count > 0)
                _lastVoiceProcessed = toVoice[^1];
        }

        foreach (var m in toVoice)
        {
            // P451 — a chat whose VoiceOver is one of the real station types'
            // under-attack / critical ids (sent by the defending-team alert in
            // FedSrvSiteBase::DamageStationEvent) plays the matching clip. Unlike
            // quick-chat, the caption NEED NOT equal the text: the server's alert
            // message ("<base> in <sector> under attack by <pilot>") is never a
            // quick-chat caption, so the match is purely on the sound id.
            if (SoundManager.TryGetStationAttackStem(m.VoiceOver, out var attackStem))
            {
                _sound.PlayVoice(attackStem);
                Console.WriteLine($"[monogame] audio: station under-attack/critical voice \"{attackStem}\" (\"{m.Message}\")");
                continue;
            }

            var e = _quickChatDb.ByVoiceOver(m.VoiceOver);
            if (e is not null && e.SoundStem.Length > 0
                && string.Equals(m.Message, e.Caption, StringComparison.Ordinal))
            {
                _sound.PlayVoice(e.SoundStem);
                Console.WriteLine($"[monogame] audio: quick-chat voice \"{e.SoundStem}\" (\"{m.Message}\")");
            }
        }
    }

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name,
        ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL",
        MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer,
        Time = 1234u,
        DwCookie = 0x33Fu,
        CrcFileList = -1,
        SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0,
        SteamID = 70000000000000000ul,
        DrmHash = "monogame",
    };
}
