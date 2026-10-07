using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.MonoGame.Capture;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Content;
using Allegiance.Core.Visual;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;
using SimVec = Allegiance.Core.Collision.Vector3;
using SimOri = Allegiance.Core.Sim.Orientation;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P565 — the offline hull inspector (<c>--inspect</c>): a client-only review mode
/// that loads the static core + art, opens an empty sector scene and shows ONE hull at
/// a time under a mouse-controlled orbit/zoom/pan camera, with a fly-by camera, look
/// toggles (native|meshy, cloak, wireframe/normals/back-faces, lighting variants, soft/
/// bloom), a VFX sandbox (engine trail, damage smoke, explosions, projectile streams,
/// missile volleys, mines — all freely combinable, plus a chaos preset), F11 screenshots
/// and a "mark bad" ledger. Runs entirely OFFLINE — no server, nothing on the wire. This
/// partial drives a dedicated <see cref="ClientState.Inspecting"/> branch in Update/Draw
/// and reuses every real renderer the flight path does (native + Meshy ship renderers,
/// the P546/P547 enhanced VFX stack, projectile/missile/mine renderers).
/// </summary>
public sealed partial class Game1
{
    private enum InspectCamMode { Orbit, FlyBy }

    // ---- configuration (from LaunchOptions.Inspect) ----
    private string? _inspectCivArg;    // --inspect-civ id|name (optional)
    private string? _inspectHullArg;   // --inspect-hull id|name (optional)

    /// <summary>P565 — the committed per-hull art-override file path (repo-relative, so it
    /// is committed alongside the code and honored when the client runs from the repo root;
    /// writable by the inspector's "keep this choice" action).</summary>
    private static string ShipArtOverridePath()
        => Path.Combine(Directory.GetCurrentDirectory(), "assets", "ships", "ship-art.json");

    // ---- live inspector state ----
    private List<InspectorHullCatalog.InspectorHull> _inspectHulls = new();
    private string? _inspectCiv;              // current civ filter (null = all)
    private int _inspectIndex;
    private bool _inspectIncludeDrones;
    private InspectCamMode _inspectCam = InspectCamMode.Orbit;
    private readonly InspectorOrbitCamera _inspectOrbit = new();
    private readonly InspectorFlyBy _inspectFlyBy = new();
    private bool _inspectAutoOrbit;
    private float _inspectCloak;              // 0 = off; else alpha fraction
    private bool _inspectSideBySide;
    private bool _inspectWireframe, _inspectNormals, _inspectBackfaces, _inspectSideTint;
    private int _inspectLightingVariant;      // 0 stock, 1 bright, 2 dark, 3 Alderan
    private bool _inspectShowNative = true, _inspectShowMeshy = true;
    private bool _inspectSoft, _inspectBloom;
    private float _inspectTimeScale = 1f;
    private bool _inspectPaused;
    private bool _inspectChaos;
    private bool _inspectEngine, _inspectSmoke, _inspectExplosion, _inspectProjectiles,
        _inspectMissiles, _inspectMines;
    private float _inspectHullFraction = 0.5f;
    private int _inspectProjectilePicker, _inspectMissilePicker, _inspectMinePicker;
    private int _inspectTagIndex;
    private string _inspectNote = "";
    private bool _inspectFlyByHullMoves;
    private float _inspectHullTravel;

    private ShipArtOverride _inspectArtOverride = new();
    private InspectorNotes? _inspectNotes;
    private string _inspectNotesPath = "inspector-notes.jsonl";
    private string _inspectScreenshotLast = "";

    // Owned VFX simulation (advanced with the effective dt, only while inspect active).
    private readonly List<(SimVec Pos, SimVec Vel, short Type)> _inspectRounds = new();
    private readonly List<(Vector3 Pos, Vector3 Fwd, short Type)> _inspectMissileSim = new();
    private readonly List<ExplosionEntity> _inspectBlasts = new();
    private readonly List<(Vector3 Pos, float Radius, int Seed)> _inspectMineSim = new();
    private bool _inspectWantShot;
    private Matrix _inspectDebugViewProj;
    private int _inspectAutoShotsRemaining, _inspectAutoShotsRequested;
    private bool _inspectAutoAdvance;
    private int _inspectShotCooldown;
    private bool _inspectShotsDone;
    private bool _inspectDemo;
    private int _inspectDemoIndex;
    private string _inspectShotTag = "";
    private bool _inspectShotNow;
    private static readonly int InspectDemoSteps = 11;

    /// <summary>Tags cycled by Tab for the mark-bad ledger.</summary>
    private static readonly string[] MarkTags =
        ["holes", "transparent", "flat", "scale", "texture", "vfx-overlap", ""];

    private const short InspectSectorId = 1;   // the empty scene's single cluster
    private const short InspectShipId = 777;   // synthetic id keying the shared pools

    // ---- edge / mouse tracking (persistent across Update calls) ----
    private bool _inspectMouseInit;
    private int _inspectPrevMouseX, _inspectPrevMouseY, _inspectPrevScroll;
    private bool _inspectLeftWasDown;
    private long _inspectLastClickMs;
    private bool _inspectPgDnWasDown, _inspectPgUpWasDown, _inspectRBWasDown, _inspectLBWasDown;
    private bool _inspectDWasDown, _inspectRWasDown, _inspectAWasDown, _inspectFWasDown, _inspectTWasDown;
    private bool _inspectMWasDown, _inspectSWasDown, _inspectKWasDown, _inspectCWasDown, _inspectLWasDown;
    private bool _inspectQWasDown, _inspectOWasDown, _inspectXWasDown, _inspectZWasDown, _inspectVWasDown, _inspectYWasDown;
    private bool _inspectNum1WasDown, _inspectNum2WasDown, _inspectNum3WasDown, _inspectNum4WasDown, _inspectNum5WasDown;
    private bool _inspectNum6WasDown, _inspectNum9WasDown, _inspectNum0WasDown;
    private bool _inspectHomeWasDown, _inspectEndWasDown, _inspectCommaWasDown, _inspectPeriodWasDown;
    private bool _inspectSemiWasDown, _inspectQuoteWasDown, _inspectQuestWasDown;
    private bool _inspectPlusWasDown, _inspectMinusWasDown, _inspectTabWasDown, _inspectEnterWasDown, _inspectF11WasDown;

    // ---------------------------------------------------------------------
    // Setup
    // ---------------------------------------------------------------------

    private void InitializeInspector()
    {
        if (_shopCatalog is null)
        {
            Console.WriteLine("[inspect] FATAL: no static core (ShopCatalog) — cannot enumerate hulls.");
            Exit();
            return;
        }
        _inspectNotes = new InspectorNotes(_inspectNotesPath);

        // Per-hull art override (assets/ships/ship-art.json), the curated good-Meshy list.
        string artFile = ShipArtOverridePath();
        if (System.IO.File.Exists(artFile))
        {
            _inspectArtOverride = ShipArtOverride.LoadFile(artFile);
            Console.WriteLine($"[inspect] ship-art override: {_inspectArtOverride.Count} entries from {artFile}");
        }

        Func<DataHullTypeIGC, bool> nativeAvail = h =>
            !string.IsNullOrEmpty(h.ModelName) &&
            _nativeShipRenderer.MeshFor(h.ModelName.Trim()) is not null;

        _inspectHulls = InspectorHullCatalog.Enumerate(
            _shopCatalog.Hulls, _hullResolver, includeDrones: _inspectIncludeDrones, nativeAvail);

        if (_inspectHulls.Count == 0)
        {
            Console.WriteLine("[inspect] no hulls enumerated — aborting.");
            Exit();
            return;
        }

        if (!string.IsNullOrEmpty(_inspectCivArg))
        {
            var civs = InspectorHullCatalog.Civs(_inspectHulls);
            string c = _inspectCivArg!;
            var match = civs.FirstOrDefault(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase));
            if (match is not null) _inspectCiv = match;
            else Console.WriteLine($"[inspect] --inspect-civ '{c}' not found; showing all civs");
        }
        if (!string.IsNullOrEmpty(_inspectHullArg))
        {
            int idx = _inspectHulls.FindIndex(h =>
                string.Equals(h.Name, _inspectHullArg, StringComparison.OrdinalIgnoreCase)
                || h.HullId.ToString() == _inspectHullArg
                || string.Equals(h.ModelName, _inspectHullArg, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _inspectIndex = idx;
            else Console.WriteLine($"[inspect] --inspect-hull '{_inspectHullArg}' not found; using first hull");
        }

        SelectInspectHull(reannounce: true);
        Window.Title = $"{_windowTitleBase} [INSPECT]";
        if (_inspectDemo)
            Console.WriteLine("[inspect] DEMO mode: scripted showcase sequence (close-up / cloak / damage+engine+explosion / guns+missiles / chaos / side-by-side / fly-by)");
        else
            Console.WriteLine($"[inspect] READY: {_inspectHulls.Count} hulls; F11 screenshot, Enter=mark bad, ?=help");
    }

    private void SelectInspectHull(bool reannounce)
    {
        var list = VisibleInspectHulls();
        if (list.Count == 0) return;
        _inspectIndex = Math.Clamp(_inspectIndex, 0, list.Count - 1);
        var h = list[_inspectIndex];
        float len = h.Length > 0f ? h.Length : 400f;
        float radius = 0.5f * len;
        // Always frame each newly-selected hull for a full view (native preview framing:
        // eye at ~2.4 × radius). In orbit mode this restores a comfortable review distance.
        if (_inspectCam == InspectCamMode.Orbit)
            _inspectOrbit.FrameRadius(radius, DockedHullPreviewFraming.FovRadians);
        if (reannounce)
            Console.WriteLine($"[inspect] hull {_inspectIndex + 1}/{list.Count}: id={h.HullId} name=\"{h.Name}\" " +
                $"civ={h.CivKey ?? "-"} model={h.ModelName} length={h.Length:0.#} native={h.NativeAvailable} " +
                $"meshy={h.MeshyAsset ?? "-"} fallback={h.FallbackUsed}");
    }

    private List<InspectorHullCatalog.InspectorHull> VisibleInspectHulls()
    {
        var all = _inspectIncludeDrones
            ? _inspectHulls
            : _inspectHulls.Where(h => !h.IsDrone).ToList();
        if (_inspectCiv is null) return all;
        return InspectorHullCatalog.ForCiv(all, _inspectCiv);
    }

    private InspectorHullCatalog.InspectorHull? CurrentInspectHull()
    {
        var list = VisibleInspectHulls();
        if (list.Count == 0) return null;
        return list[Math.Clamp(_inspectIndex, 0, list.Count - 1)];
    }

    // ---------------------------------------------------------------------
    // Update (input)
    // ---------------------------------------------------------------------

    private void UpdateInspector(GameTime gameTime, KeyboardState ks)
    {
        // Unattended visual-proof hook: after each autoshot, advance to the next hull so
        // consecutive shots review different hulls.
        if (_inspectAutoAdvance)
        {
            _inspectAutoAdvance = false;
            _inspectIndex++;
            SelectInspectHull(reannounce: false);
        }

        _inspectSoft = _softEnabled;
        _inspectBloom = _bloomEnabled;
        float effDt = _inspectPaused
            ? 0f
            : (float)gameTime.ElapsedGameTime.TotalSeconds * _inspectTimeScale;

        // ---- mouse camera ----
        var ms = Mouse.GetState();
        if (!_inspectMouseInit)
        {
            _inspectMouseInit = true;
            _inspectPrevMouseX = ms.X; _inspectPrevMouseY = ms.Y; _inspectPrevScroll = ms.ScrollWheelValue;
        }
        int dx = ms.X - _inspectPrevMouseX, dy = ms.Y - _inspectPrevMouseY;
        _inspectPrevMouseX = ms.X; _inspectPrevMouseY = ms.Y;
        bool left = ms.LeftButton == ButtonState.Pressed;
        bool right = ms.RightButton == ButtonState.Pressed;
        bool middle = ms.MiddleButton == ButtonState.Pressed;
        if (_inspectCam == InspectCamMode.Orbit)
        {
            if (left) _inspectOrbit.Orbit(dx, dy);
            else if (right || middle) _inspectOrbit.Pan(dx, dy);
            if (ms.ScrollWheelValue != _inspectPrevScroll)
            {
                int delta = (ms.ScrollWheelValue - _inspectPrevScroll) / 120;
                _inspectOrbit.Zoom(delta);
                _inspectPrevScroll = ms.ScrollWheelValue;
            }
            long nowMs = Environment.TickCount64;
            bool justPressed = ms.LeftButton == ButtonState.Pressed && !_inspectLeftWasDown;
            if (justPressed)
            {
                if (nowMs - _inspectLastClickMs < 320) _inspectOrbit.Focus(Vector3.Zero);
                _inspectLastClickMs = nowMs;
            }
            _inspectLeftWasDown = ms.LeftButton == ButtonState.Pressed;
            if (_inspectAutoOrbit) _inspectOrbit.Step(effDt);
        }

        // ---- fly-by ----
        if (_inspectCam == InspectCamMode.FlyBy)
        {
            if (!_inspectFlyByHullMoves) _inspectFlyBy.Step(effDt);
            else _inspectHullTravel = _inspectFlyBy.Loop
                ? (_inspectHullTravel + effDt / _inspectFlyBy.DurationSeconds) % 1f
                : Math.Clamp(_inspectHullTravel + effDt / _inspectFlyBy.DurationSeconds, 0f, 1f);
        }

        // ---- keys ----
        bool Edge(ref bool was, XnaKeys k) { bool d = ks.IsKeyDown(k); bool r = d && !was; was = d; return r; }

        if (Edge(ref _inspectPgDnWasDown, XnaKeys.PageDown)) { _inspectIndex++; SelectInspectHull(true); }
        if (Edge(ref _inspectPgUpWasDown, XnaKeys.PageUp)) { _inspectIndex--; SelectInspectHull(true); }
        if (Edge(ref _inspectRBWasDown, XnaKeys.OemCloseBrackets)) CycleInspectCiv(+1);
        if (Edge(ref _inspectLBWasDown, XnaKeys.OemOpenBrackets)) CycleInspectCiv(-1);

        if (Edge(ref _inspectDWasDown, XnaKeys.D)) { _inspectIncludeDrones = !_inspectIncludeDrones; _inspectIndex = 0; SelectInspectHull(true); }
        if (Edge(ref _inspectRWasDown, XnaKeys.R)) { _inspectCam = InspectCamMode.Orbit; _inspectOrbit.Reset(0f); }
        if (Edge(ref _inspectAWasDown, XnaKeys.A)) { _inspectAutoOrbit = !_inspectAutoOrbit; _inspectOrbit.AutoOrbit = _inspectAutoOrbit; }

        if (Edge(ref _inspectFWasDown, XnaKeys.F))
        {
            if (_inspectCam == InspectCamMode.Orbit)
            {
                _inspectCam = InspectCamMode.FlyBy;
                _inspectFlyBy.Target = Vector3.Zero;
                _inspectFlyBy.ApproachDistance = 2200f;
                var h = CurrentInspectHull();
                _inspectFlyBy.PassOffset = h is null ? 220f : Math.Max(0.5f * h.Length, 120f);
                _inspectFlyBy.Restart();
            }
            else _inspectCam = InspectCamMode.Orbit;
        }
        if (_inspectCam == InspectCamMode.FlyBy && Edge(ref _inspectTWasDown, XnaKeys.T))
        {
            _inspectFlyByHullMoves = !_inspectFlyByHullMoves;
            _inspectHullTravel = 0f;
        }

        if (Edge(ref _inspectMWasDown, XnaKeys.M) && !_inspectSideBySide)
        {
            _inspectShowNative = !_inspectShowNative;
            _inspectShowMeshy = !_inspectShowNative;
        }
        if (Edge(ref _inspectSWasDown, XnaKeys.S))
        {
            _inspectSideBySide = !_inspectSideBySide;
            if (_inspectSideBySide) { _inspectShowNative = true; _inspectShowMeshy = true; }
        }
        if (Edge(ref _inspectKWasDown, XnaKeys.K)) SaveInspectArtChoice();

        if (Edge(ref _inspectCWasDown, XnaKeys.C)) _inspectCloak = _inspectCloak > 0f ? 0f : 0.35f;
        if (Edge(ref _inspectPlusWasDown, XnaKeys.OemPlus)) _inspectCloak = Math.Clamp(_inspectCloak + 0.1f, 0f, 1f);
        if (Edge(ref _inspectMinusWasDown, XnaKeys.OemMinus)) _inspectCloak = Math.Clamp(_inspectCloak - 0.1f, 0f, 1f);

        if (Edge(ref _inspectLWasDown, XnaKeys.L)) { _inspectLightingVariant = (_inspectLightingVariant + 1) % 4; }
        if (Edge(ref _inspectQWasDown, XnaKeys.Q)) { _inspectSoft = !_inspectSoft; }
        if (Edge(ref _inspectOWasDown, XnaKeys.O)) { _inspectBloom = !_inspectBloom; }

        if (Edge(ref _inspectXWasDown, XnaKeys.X)) _inspectWireframe = !_inspectWireframe;
        if (Edge(ref _inspectZWasDown, XnaKeys.Z)) _inspectNormals = !_inspectNormals;
        if (Edge(ref _inspectVWasDown, XnaKeys.V)) _inspectBackfaces = !_inspectBackfaces;
        if (Edge(ref _inspectYWasDown, XnaKeys.Y)) _inspectSideTint = !_inspectSideTint;

        // VFX toggles
        if (Edge(ref _inspectNum1WasDown, XnaKeys.D1)) { _inspectEngine = !_inspectEngine; }
        if (Edge(ref _inspectNum2WasDown, XnaKeys.D2)) { _inspectSmoke = !_inspectSmoke; }
        if (Edge(ref _inspectNum3WasDown, XnaKeys.D3)) FireInspectExplosion();
        if (Edge(ref _inspectNum4WasDown, XnaKeys.D4))
        {
            _inspectProjectiles = !_inspectProjectiles;
            if (_inspectProjectiles) for (int i = 0; i < 12; i++) FireInspectRound();
        }
        if (Edge(ref _inspectNum5WasDown, XnaKeys.D5))
        {
            _inspectMissiles = !_inspectMissiles;
            if (_inspectMissiles) LaunchInspectMissiles(6);
        }
        if (Edge(ref _inspectNum6WasDown, XnaKeys.D6))
        {
            _inspectMines = !_inspectMines;
            if (_inspectMines) FieldInspectMines(40);
        }
        if (Edge(ref _inspectNum9WasDown, XnaKeys.D9)) ToggleInspectChaos();
        if (Edge(ref _inspectNum0WasDown, XnaKeys.D0)) _inspectPaused = !_inspectPaused;

        if (Edge(ref _inspectHomeWasDown, XnaKeys.Home)) _inspectTimeScale = Math.Max(0.05f, _inspectTimeScale - 0.25f);
        if (Edge(ref _inspectEndWasDown, XnaKeys.End)) _inspectTimeScale = Math.Min(2f, _inspectTimeScale + 0.25f);

        if (Edge(ref _inspectCommaWasDown, XnaKeys.OemComma)) CycleInspectProjectile(-1);
        if (Edge(ref _inspectPeriodWasDown, XnaKeys.OemPeriod)) CycleInspectProjectile(+1);
        if (Edge(ref _inspectSemiWasDown, XnaKeys.OemSemicolon)) CycleInspectMissile(-1);
        if (Edge(ref _inspectQuoteWasDown, XnaKeys.OemQuotes)) CycleInspectMissile(+1);
        if (Edge(ref _inspectQuestWasDown, XnaKeys.OemQuestion)) CycleInspectMine(+1);

        if (Edge(ref _inspectTabWasDown, XnaKeys.Tab)) _inspectTagIndex = (_inspectTagIndex + 1) % MarkTags.Length;
        if (Edge(ref _inspectEnterWasDown, XnaKeys.Enter)) MarkInspectBad();
        if (Edge(ref _inspectF11WasDown, XnaKeys.F11)) _inspectWantShot = true;

        // ghost projectiles even when the stream toggle was pressed once: keep streaming
        if (_inspectProjectiles) FireInspectRoundContinuously(effDt);

        StepInspectVfx(effDt);
    }

    private void FireInspectRoundContinuously(float effDt)
    {
        _inspectStreamAccum += effDt;
        if (_inspectStreamAccum >= 0.045f)
        {
            _inspectStreamAccum = 0f;
            FireInspectRound();
        }
    }
    private float _inspectStreamAccum;

    private void ToggleInspectChaos()
    {
        _inspectChaos = !_inspectChaos;
        if (_inspectChaos)
        {
            _inspectEngine = _inspectSmoke = _inspectExplosion = _inspectProjectiles =
                _inspectMissiles = _inspectMines = true;
            FireInspectExplosion();
            LaunchInspectMissiles(6);
            FieldInspectMines(40);
            Console.WriteLine("[inspect] CHAOS preset ON");
        }
        else
        {
            _inspectEngine = _inspectSmoke = _inspectExplosion = _inspectProjectiles =
                _inspectMissiles = _inspectMines = false;
        }
    }

    private void CycleInspectCiv(int dir)
    {
        var civs = InspectorHullCatalog.Civs(_inspectHulls);
        if (civs.Count == 0) return;
        int cur = _inspectCiv is null ? -1 : civs.FindIndex(c => string.Equals(c, _inspectCiv, StringComparison.OrdinalIgnoreCase));
        int next = cur < 0 ? (dir > 0 ? 0 : civs.Count - 1) : (cur + dir + civs.Count) % civs.Count;
        _inspectCiv = civs[next];
        _inspectIndex = 0;
        SelectInspectHull(true);
        Console.WriteLine($"[inspect] civ: {_inspectCiv} ({VisibleInspectHulls().Count} hulls)");
    }

    private void CycleInspectProjectile(int dir)
    {
        if (_shopCatalog?.ProjectileTypes is not { } pts || pts.Count == 0) return;
        var keys = pts.Keys.OrderBy(k => k).ToList();
        _inspectProjectilePicker = (_inspectProjectilePicker + dir + keys.Count) % keys.Count;
        Console.WriteLine($"[inspect] projectile picker: type {keys[_inspectProjectilePicker]} " +
            $"(model {pts[keys[_inspectProjectilePicker]].ModelName}); press 4 to stream");
    }

    private void CycleInspectMissile(int dir)
    {
        if (_shopCatalog?.MissileTypes is not { } mts || mts.Count == 0) return;
        var keys = mts.Keys.OrderBy(k => k).ToList();
        _inspectMissilePicker = (_inspectMissilePicker + dir + keys.Count) % keys.Count;
        Console.WriteLine($"[inspect] missile picker: type {keys[_inspectMissilePicker]} " +
            $"(name {mts[keys[_inspectMissilePicker]].LauncherName}); press 5 to volley");
    }

    private void CycleInspectMine(int dir)
    {
        if (_shopCatalog?.MineTypes is not { } mns || mns.Count == 0) return;
        var keys = mns.Keys.OrderBy(k => k).ToList();
        _inspectMinePicker = (_inspectMinePicker + dir + keys.Count) % keys.Count;
        Console.WriteLine($"[inspect] mine picker: type {keys[_inspectMinePicker]} " +
            $"(name {mns[keys[_inspectMinePicker]].LauncherName}); press 6 to field");
    }

    private void SaveInspectArtChoice()
    {
        var h = CurrentInspectHull();
        if (h is null) return;
        var choice = _inspectShowNative ? ShipArtOverride.ArtChoice.Native : ShipArtOverride.ArtChoice.Meshy;
        _inspectArtOverride.SetByModel(h.ModelName, choice);
        string artFile = ShipArtOverridePath();
        _inspectArtOverride.Save(artFile);
        Console.WriteLine($"[inspect] KEEP {h.ModelName} as {choice} -> {artFile}");
    }

    private void MarkInspectBad()
    {
        var h = CurrentInspectHull();
        if (h is null || _inspectNotes is null) return;
        string tag = MarkTags[Math.Clamp(_inspectTagIndex, 0, MarkTags.Length - 1)];
        string? tagNull = string.IsNullOrEmpty(tag) ? null : tag;
        _inspectNotes.Append(new InspectorNote(
            DateTimeOffset.Now, _inspectScreenshotLast, h.HullId, h.Name, h.CivKey,
            _inspectCam == InspectCamMode.FlyBy ? "flyby" : "orbit",
            _inspectOrbit.Yaw, _inspectOrbit.Pitch, _inspectOrbit.Distance,
            _inspectOrbit.Target.X, _inspectOrbit.Target.Y, _inspectOrbit.Target.Z,
            InspectToggles(), InspectActiveVfx(), tagNull,
            string.IsNullOrWhiteSpace(_inspectNote) ? null : _inspectNote.Trim()));
        Console.WriteLine($"[inspect] MARKED BAD hull {h.Name} (id {h.HullId}) tag='{tagNull ?? "-"}'");
        _inspectNote = "";
    }

    private List<string> InspectToggles() => new()
    {
        $"art={( _inspectShowNative && _inspectShowMeshy ? "both" : _inspectShowNative ? "native" : "meshy" )}",
        $"cloak={(_inspectCloak > 0f ? _inspectCloak.ToString("0.##") : "off")}",
        $"lighting={_inspectLightingVariant}",
        $"wireframe={_inspectWireframe}",
        $"normals={_inspectNormals}",
        $"backfaces={_inspectBackfaces}",
        $"soft={_inspectSoft}",
        $"bloom={_inspectBloom}",
    };

    private List<string> InspectActiveVfx()
    {
        var v = new List<string>();
        if (_inspectEngine) v.Add("engine");
        if (_inspectSmoke) v.Add($"smoke:{_inspectHullFraction:P0}");
        if (_inspectExplosion) v.Add("explosion");
        if (_inspectProjectiles) v.Add("projectiles");
        if (_inspectMissiles) v.Add("missiles");
        if (_inspectMines) v.Add("mines");
        return v;
    }

    // ---- VFX simulation (owned by the inspector; stepped with effDt) ----

    private void FireInspectExplosion()
    {
        var h = CurrentInspectHull();
        float radius = h is null ? 100f : 0.5f * h.Length;
        _inspectBlasts.Add(new ExplosionEntity
        {
            Type = ExplosionType.SmallShip,
            Position = new SimVec(radius * 0.7f, radius * 0.3f, -radius * 0.6f),
            ClusterId = InspectSectorId,
            Velocity = SimVec.Zero,
            ReceivedLocalMs = Environment.TickCount64,
        });
        _inspectExplosion = true;
        Console.WriteLine("[inspect] explosion fired near hull");
    }

    private void LaunchInspectMissiles(int count)
    {
        if (_shopCatalog?.MissileTypes is not { } mts || mts.Count == 0) return;
        var keys = mts.Keys.OrderBy(k => k).ToList();
        var key = keys[Math.Clamp(_inspectMissilePicker, 0, keys.Count - 1)];
        short type = key;
        var rnd = new Random(42);
        for (int i = 0; i < count; i++)
        {
            var fwd = Vector3.Normalize(new Vector3(
                (float)(rnd.NextDouble() * 2 - 1), (float)(rnd.NextDouble() * 0.6 - 0.3), -1f));
            _inspectMissileSim.Add((new Vector3(0f, 0f, 0f), fwd, type));
        }
        Console.WriteLine($"[inspect] launched {count} missiles of type {key}");
    }

    private void FieldInspectMines(int count)
    {
        if (_shopCatalog?.MineTypes is not { } mns || mns.Count == 0) return;
        var keys = mns.Keys.OrderBy(k => k).ToList();
        var key = keys[Math.Clamp(_inspectMinePicker, 0, keys.Count - 1)];
        float radius = MineModelSource.ResolveRadius(key, mns);
        var rnd = new Random(7);
        for (int i = 0; i < count; i++)
        {
            var p = new Vector3(
                (float)(rnd.NextDouble() * 1200 - 600),
                (float)(rnd.NextDouble() * 400 - 200),
                (float)(rnd.NextDouble() * 1200 - 600));
            _inspectMineSim.Add((p, radius, i));
        }
        Console.WriteLine($"[inspect] fielded {count} mines of type {key}");
    }

    private void FireInspectRound()
    {
        var h = CurrentInspectHull();
        float r = h is null ? 50f : Math.Max(0.5f * h.Length, 5f);
        var pts = _shopCatalog?.ProjectileTypes;
        if (pts is null || pts.Count == 0) return;
        var keys = pts.Keys.OrderBy(k => k).ToList();
        var key = keys[Math.Clamp(_inspectProjectilePicker, 0, keys.Count - 1)];
        short type = key;
        var pt = pts[type];
        float speed = pt.Speed > 0f ? pt.Speed : 400f;
        // stream across the hull from the side
        float y = (float)(Math.Sin(_inspectRoundTick) * r * 0.6);
        _inspectRoundTick++;
        _inspectRounds.Add((new SimVec(-r * 2.2f, y, r * 0.3f), new SimVec(speed, 0f, -r * 0.1f), type));
    }
    private int _inspectRoundTick;

    private void StepInspectVfx(float effDt)
    {
        for (int i = _inspectRounds.Count - 1; i >= 0; i--)
        {
            var r = _inspectRounds[i];
            r.Pos = new SimVec(r.Pos.X + r.Vel.X * effDt, r.Pos.Y + r.Vel.Y * effDt, r.Pos.Z + r.Vel.Z * effDt);
            if (r.Pos.LengthSquared() > 6000f * 6000f) _inspectRounds.RemoveAt(i);
        }
        for (int i = _inspectMissileSim.Count - 1; i >= 0; i--)
        {
            var m = _inspectMissileSim[i];
            m.Pos += m.Fwd * (GetMissileSpeed(m.Type) * effDt);
            if (m.Pos.Length() > 6000f) _inspectMissileSim.RemoveAt(i);
        }
        long now = Environment.TickCount64;
        _inspectBlasts.RemoveAll(b => now - b.ReceivedLocalMs > 5000);
    }

    private float GetMissileSpeed(short type)
    {
        if (_shopCatalog?.MissileTypes is { } mts && mts.TryGetValue(type, out var mt)) return mt.InitialSpeed > 0f ? mt.InitialSpeed : 300f;
        return 300f;
    }

    // ---------------------------------------------------------------------
    // Draw
    // ---------------------------------------------------------------------

    private void DrawInspector(GameTime gameTime)
    {
        var gd = GraphicsDevice;
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;

        // Demo mode: apply the next showcase step BEFORE the camera is computed, so the
        // step's camera/VFX state (close-up zoom, fly-by phase, chaos) is what this frame
        // renders and the screenshot at the end captures it.
        if (_inspectDemo)
        {
            _inspectShotCooldown--;
            if (_inspectShotCooldown <= 0 && _nowSeconds > 1.0 && _inspectDemoIndex < InspectDemoSteps)
            {
                InspectDemoApply(_inspectDemoIndex);
                _inspectDemoIndex++;
                _inspectShotCooldown = 45;
                _inspectShotNow = true;
            }
        }

        bool bloom = _inspectBloom && _post is { BloomReady: true };
        bool soft = _inspectSoft && _post is { SoftReady: true };
        float aspect = gd.Viewport.AspectRatio;
        float fov = MathHelper.ToRadians(_flightFovDeg);

        Vector3 cameraEye;
        Matrix view, proj;
        if (_inspectCam == InspectCamMode.FlyBy && !_inspectFlyByHullMoves)
        {
            view = _inspectFlyBy.View(out cameraEye, out _);
        }
        else
        {
            cameraEye = _inspectOrbit.Eye;
            view = _inspectOrbit.View();
        }
        proj = Matrix.CreatePerspectiveFieldOfView(fov, aspect, 0.5f, 12000f);

        // soft-depth prep (P559 pattern: depth pass before color so soft smoke reads hulls)
        if (soft)
        {
            _softContext = _post!.Context(LinearDepth.DefaultNear, LinearDepth.DefaultFar);
            gd.SetRenderTarget(_post.DepthRT);
            gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, VfxPostProcessor.DepthClearFar, 1f, 0);
            DrawInspectDepth(view, proj, cameraEye);
            gd.SetRenderTarget(bloom ? _post.SceneRT : (RenderTarget2D?)null);
            gd.BlendState = BlendState.Opaque;
            gd.DepthStencilState = DepthStencilState.Default;
        }
        else _softContext = null;

        if (bloom) { gd.SetRenderTarget(_post!.SceneRT); gd.Clear(Color.Black); }
        else gd.Clear(Color.Black);

        _sectorLighting = InspectSectorLighting();

        _starFar.Draw(view, proj, cameraEye);
        _starMid.Draw(view, proj, cameraEye);
        _starNear.Draw(view, proj, cameraEye);

        DrawInspectHulls(view, proj, cameraEye);

        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        float effDt = _inspectPaused ? 0f : dt * _inspectTimeScale;
        DrawInspectVfx(view, proj, cameraEye, dt, effDt);

        if (bloom)
        {
            gd.SetRenderTarget(null);
            _post!.Bloom!.Render(_post.SceneRT!);
        }

        DrawInspectHud(gd.Viewport);

        // Capture: an in-frame demo shot, F11 (interactive), or the unattended sweep.
        if (_inspectShotNow)
        {
            _inspectShotNow = false;
            _inspectScreenshotLast = TakeInspectScreenshot();
            if (_inspectDemo && _inspectDemoIndex >= InspectDemoSteps && !_inspectShotsDone)
            {
                _inspectShotsDone = true;
                Console.WriteLine($"[inspect] demo sequence complete ({InspectDemoSteps} steps) — exiting");
                Exit();
            }
        }
        else if (_inspectWantShot)
        {
            _inspectWantShot = false;
            _inspectScreenshotLast = TakeInspectScreenshot();
        }
        else if (!_inspectDemo && _inspectAutoShotsRemaining > 0 && _inspectShotCooldown <= 0 && _nowSeconds > 1.0)
        {
            _inspectScreenshotLast = TakeInspectScreenshot();
            _inspectAutoShotsRemaining--;
            _inspectShotCooldown = 40;        // ~0.7 s between shots so each hull settles
            _inspectAutoAdvance = true;
        }
        if (!_inspectDemo && _inspectAutoShotsRequested > 0 && _inspectAutoShotsRemaining == 0 && !_inspectShotsDone)
        {
            _inspectShotsDone = true;
            Console.WriteLine($"[inspect] autoshots complete ({_inspectAutoShotsRequested}) — exiting");
            Exit();
        }
    }

    private void DrawInspectDepth(Matrix view, Matrix proj, Vector3 cameraEye)
    {
        var h = CurrentInspectHull();
        if (h is null || _post?.LinearDepthEffect is not { } depthEffect) return;
        foreach (var (world, scale, native) in InspectHullDrawSlots(h))
        {
            var worldRel = world * Matrix.CreateTranslation(-cameraEye);
            if (native)
            {
                var nref = ResolveNativeShip(h.HullId);
                if (nref is not null && _nativeShipRenderer.MeshFor(nref.ModelStem!) is { } geo)
                    _nativeShipRenderer.DrawDepth(geo, worldRel, view, proj, nref.NativeScale, depthEffect, LinearDepth.DefaultNear, LinearDepth.DefaultFar);
            }
            else
            {
                var model = InspectMeshyModel(h);
                if (model is not null)
                    _shipRenderer.DrawDepth(model, worldRel, view, proj, scale, depthEffect, LinearDepth.DefaultNear, LinearDepth.DefaultFar);
            }
        }
    }

    private void DrawInspectHulls(Matrix view, Matrix proj, Vector3 cameraEye)
    {
        var h = CurrentInspectHull();
        if (h is null) return;
        float opacity = _inspectCloak > 0f ? _inspectCloak : 1f;
        foreach (var (slotWorld, scale, native) in InspectHullDrawSlots(h))
        {
            var world = _inspectCam == InspectCamMode.FlyBy && _inspectFlyByHullMoves
                ? Matrix.CreateTranslation(InspectHullTravelPos()) * slotWorld
                : slotWorld;
            var worldRel = world * Matrix.CreateTranslation(-cameraEye);
            bool drew = false;
            if (native)
            {
                var nref = ResolveNativeShip(h.HullId);
                if (nref is not null && _nativeShipRenderer.MeshFor(nref.ModelStem!) is { } geo)
                {
                    var ntex = nref.TextureStem is not null ? _nativeShipRenderer.TextureFor(nref.TextureStem) : null;
                    _nativeShipRenderer.Draw(geo, ntex, worldRel, view, proj, opacity, nref.NativeScale, _sectorLighting);
                    drew = true;
                }
            }
            if (!drew)
            {
                var model = InspectMeshyModel(h) ?? _fallbackModel;
                var tex = InspectMeshyTexture(h) ?? _fallbackTexture;
                _shipRenderer.Draw(model, worldRel, view, proj, opacity, scale, tex, _sectorLighting);
            }
        }
    }

    /// <summary>The hull's draw slots: either the single active art source, or BOTH (side
    /// by side — native offset -X, meshy +X). Returns (world, meshyScale, isNative).</summary>
    private IEnumerable<(Matrix World, float MeshyScale, bool IsNative)> InspectHullDrawSlots(InspectorHullCatalog.InspectorHull h)
    {
        bool native = _inspectShowNative, meshy = _inspectShowMeshy;
        float meshyScale = 1f;
        var model = InspectMeshyModel(h);
        if (model is not null && model != _fallbackModel)
            meshyScale = ShipRenderScale.ScaleForLength(h.Length, model);
        if (_inspectSideBySide)
        {
            if (native) yield return (Matrix.CreateTranslation(new Vector3(-h.Length * 0.9f, 0f, 0f)), meshyScale, true);
            if (meshy) yield return (Matrix.CreateTranslation(new Vector3(h.Length * 0.9f, 0f, 0f)), meshyScale, false);
            yield break;
        }
        if (_inspectShowNative) yield return (Matrix.Identity, meshyScale, true);
        else yield return (Matrix.Identity, meshyScale, false);
    }

    private Vector3 InspectHullTravelPos()
    {
        var start = -_inspectFlyBy.Axis * _inspectFlyBy.ApproachDistance;
        var end = _inspectFlyBy.Axis * _inspectFlyBy.ApproachDistance;
        return Vector3.Lerp(start, end, _inspectHullTravel);
    }

    private XnaModel? InspectMeshyModel(InspectorHullCatalog.InspectorHull h)
    {
        if (h.MeshyAsset is not null && _shipModels.TryGetValue(h.MeshyAsset, out var m)) return m;
        var civ = h.CivKey;
        if (civ is not null)
        {
            var asset = _hullResolver.ResolveAsset(civ, HullById(h.HullId));
            if (asset is not null && _shipModels.TryGetValue(asset, out var m2)) return m2;
        }
        return null;
    }

    private Texture2D? InspectMeshyTexture(InspectorHullCatalog.InspectorHull h)
    {
        if (h.MeshyAsset is not null && _shipTextures.TryGetValue(h.MeshyAsset, out var t)) return t;
        return null;
    }

    private DataHullTypeIGC HullById(short id)
        => _hullById is { } m && m.TryGetValue(id, out var hh) ? hh : default;

    private void DrawInspectVfx(Matrix view, Matrix proj, Vector3 cameraEye, float dt, float effDt)
    {
        if (dt <= 0f) dt = 1f / 60f;
        var h = CurrentInspectHull();
        float radius = h is null ? 50f : Math.Max(0.5f * h.Length, 1f);

        // engine trail + nozzle glow (real pools, keyed by hull id)
        if (_inspectEngine)
        {
            _shipThrustPower[InspectShipId] = 1f;
            _engineTrailEmitters[InspectShipId] = new EngineTrailEmitter(SimVec.Zero, SimVec.Zero, SimOri.Identity, radius);
            _nozzleGlowEmitters[InspectShipId] = new EngineNozzleGlowEmitter(SimVec.Zero, SimOri.Identity, radius, 1f);
        }
        else { _engineTrailEmitters.Remove(InspectShipId); _nozzleGlowEmitters.Remove(InspectShipId); }

        foreach (var kv in _engineTrailEmitters)
        {
            var et = kv.Value;
            _engineTrail.Emit(new EngineTrailShip(kv.Key, et.Position, et.Velocity,
                et.Orientation, et.Radius, _shipThrustPower.GetValueOrDefault(kv.Key)), effDt, _trailRandom);
        }
        _engineTrail.Update(effDt);
        _drawEngineTrailViews.Clear();
        _drawEngineTrailViews.AddRange(_engineTrail.Enumerate());
        _engineTrailRenderer.Draw(_drawEngineTrailViews, cameraEye, view, proj, _sectorLighting,
            ParticleNearFade.Chase, ParticleFarFade.Flight, default, _softContext);

        // nozzle glow
        _drawNozzleGlows.Clear();
        foreach (var kv in _nozzleGlowEmitters)
        {
            var ng = kv.Value;
            var (nr, ng_, nb, scale) = EngineNozzleGlow.Compute(ng.Value);
            var backward = ng.Orientation.GetBackward();
            var glowPos = ng.Position + (backward * (ng.Radius * EngineNozzleGlowRenderer.AftCenterFraction));
            float half = scale * ng.Radius * EngineNozzleGlowRenderer.GlowSizeFactor;
            _drawNozzleGlows.Add(new EngineGlowView(glowPos, half, new Vector3(nr, ng_, nb)));
        }
        _nozzleGlowRenderer.Draw(_drawNozzleGlows, cameraEye, view, proj);

        // damage smoke
        if (_inspectSmoke)
        {
            int vents = DamageDebrisTracker.DamageFrameCount(_inspectHullFraction);
            if (vents > 0)
                _damageEmitters[InspectShipId] = new DamageEmitter(SimVec.Zero, SimVec.Zero, SimOri.Identity, vents, radius);
            else _damageEmitters.Remove(InspectShipId);
        }
        else _damageEmitters.Remove(InspectShipId);
        foreach (var kv in _damageEmitters)
        {
            var em = kv.Value;
            _damageDebris.Emit(em.Position, em.Velocity, em.Orientation, em.Radius, kv.Key, em.VentCount, effDt, _trailRandom);
        }
        _damageDebris.Update(effDt);
        _drawDebrisViews.Clear();
        _drawDebrisViews.AddRange(_damageDebris.Enumerate());
        _damageDebrisRenderer.Draw(_drawDebrisViews, cameraEye, view, proj, _sectorLighting,
            ParticleNearFade.Chase, ParticleFarFade.Flight, default, _softContext);

        // explosion blasts (self-expiring via the renderers' lifespan)
        _drawExplosions.Clear();
        foreach (var b in _inspectBlasts) _drawExplosions.Add(b);
        if (_drawExplosions.Count > 0)
        {
            if (_enhancedExplosionRenderer is { } enh)
                enh.Draw(_drawExplosions, InspectSectorId, Environment.TickCount64, cameraEye, view, proj, _sectorLighting,
                    ParticleNearFade.Chase, ParticleFarFade.Flight, default, _softContext);
            else
                _explosionRenderer.Draw(_drawExplosions, InspectSectorId, Environment.TickCount64, cameraEye, view, proj);
        }

        // projectiles
        foreach (var (bPos, bDir, bLen, bSpin, bVis) in InspectRoundsDraw())
        {
            var bXna = PoseConverter.ToXna(bPos);
            var bXnaDir = new Vector3(bDir.X, bDir.Y, bDir.Z);
            switch (bVis.Kind)
            {
                case ProjectileVisualKind.Billboard:
                    {
                        var tex = _projectileSpriteRenderer.TextureFor(bVis.TextureName);
                        if (tex is not null)
                        {
                            float half = bVis.Radius * ProjectileSpriteRenderer.HalfExtentScale;
                            _projectileSpriteRenderer.DrawBillboard(bXna - cameraEye, tex, half, bSpin, new Vector3(bVis.R, bVis.G, bVis.B), view, proj);
                            continue;
                        }
                        break;
                    }
                case ProjectileVisualKind.Directional:
                    {
                        var tex = _projectileSpriteRenderer.TextureFor(bVis.TextureName);
                        if (tex is not null)
                        {
                            float length = bVis.Radius > 0f ? 2f * bVis.Radius : bLen;
                            _projectileSpriteRenderer.DrawDirectional(bXna - cameraEye, bXnaDir, tex, length, bVis.EffectiveWidth, bSpin, new Vector3(bVis.R, bVis.G, bVis.B), view, proj);
                            continue;
                        }
                        break;
                    }
                case ProjectileVisualKind.Model:
                    {
                        var geo = _projectileModelRenderer.MeshFor(bVis.ModelName);
                        if (geo is not null)
                        {
                            var tex = _projectileModelRenderer.TextureFor(bVis.ModelName);
                            _projectileModelRenderer.Draw(geo, tex, bXna - cameraEye, bXnaDir, bSpin, view, proj);
                            continue;
                        }
                        break;
                    }
                default: break;
            }
            var bOrient = new Orientation(); bOrient.Set(bDir);
            var bRot = PoseConverter.ToXnaRotation(bOrient);
            var bWorld = Matrix.CreateScale(bVis.EffectiveWidth, bVis.EffectiveWidth, bLen) * bRot * Matrix.CreateTranslation(bXna - cameraEye);
            _projectileRenderer.Draw(bWorld, new Vector3(ProjectileVisual.Amber.R, ProjectileVisual.Amber.G, ProjectileVisual.Amber.B), view, proj);
        }

        // missile volley (real mesh + trail)
        _trailNowSec += dt;
        _drawTrailMissiles.Clear();
        foreach (var m in _inspectMissileSim)
        {
            var mo = new Orientation(); mo.Set(-new SimVec(m.Fwd.X, m.Fwd.Y, m.Fwd.Z));
            var mWorld = PoseConverter.ToXnaRotation(mo) * Matrix.CreateTranslation(m.Pos - cameraEye);
            _missileRenderer.Draw(mWorld, view, proj, _sectorLighting);
            _drawTrailMissiles.Add(new TrailMissile((short)m.Type,
                new SimVec(m.Pos.X, m.Pos.Y, m.Pos.Z), new SimVec(m.Fwd.X, m.Fwd.Y, m.Fwd.Z), _trailNowSec));
        }
        _trailParticles.Update(_trailNowSec, dt, _drawTrailMissiles, _trailRandom);
        _drawTrailViews.Clear();
        _drawTrailViews.AddRange(_trailParticles.Enumerate());
        _trailRenderer.Draw(_drawTrailViews, cameraEye, view, proj, _sectorLighting,
            ParticleNearFade.Chase, ParticleFarFade.Flight, default, _softContext);

        // mines
        foreach (var (mnPos, mnRadius, mnSeed) in _inspectMineSim)
            _mineRenderer.DrawCloud(mnPos - cameraEye, mnRadius, 1f, mnSeed, cameraEye, view, proj);

        // wireframe / normals debug overlay (axes + bounding box)
        if (_inspectWireframe || _inspectNormals)
            DrawInspectDebugOverlay(view, proj, cameraEye);
    }

    private IEnumerable<(SimVec Pos, SimVec Dir, float Len, float Spin, ProjectileVisual Visual)> InspectRoundsDraw()
    {
        foreach (var r in _inspectRounds)
        {
            var visual = ResolveProjectileVisual(r.Type);
            SimVec dir;
            if (r.Vel.LengthSquared() > 1e-6f) dir = r.Vel.Normalize();
            else dir = new SimVec(0f, 0f, -1f);
            float len = Math.Clamp(r.Vel.Length() * BoltLengthPerUnitSeconds, MinBoltLength, MaxBoltLength);
            yield return (r.Pos, dir, len, 0f, visual);
        }
    }

    // ---- debug overlay: local axes + bounding box, screen-projected ----

    private void DrawInspectDebugOverlay(Matrix view, Matrix proj, Vector3 cameraEye)
    {
        _inspectDebugViewProj = view * proj;
        var vp = GraphicsDevice.Viewport;
        float len = CurrentInspectHull()?.Length ?? 400f;
        var cols = new[] { Color.Red, Color.Lime, Color.Blue };
        var dirs = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        _spriteBatch.Begin();
        if (_inspectNormals)
            for (int i = 0; i < 3; i++)
                DrawWorldLine(Vector3.Zero, dirs[i] * (len * 0.75f), cols[i]);
        if (_inspectWireframe)
        {
            float he = len * 0.5f;
            var lo = new Vector3(-he, -he, -he);
            var hi = new Vector3(he, he, he);
            DrawBoundingBox(lo, hi, Color.Gray);
        }
        _spriteBatch.End();
    }

    private void DrawBoundingBox(Vector3 lo, Vector3 hi, Color c)
    {
        Vector3[] corners =
        {
            new(lo.X, lo.Y, lo.Z), new(hi.X, lo.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(lo.X, hi.Y, lo.Z),
            new(lo.X, lo.Y, hi.Z), new(hi.X, lo.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(lo.X, hi.Y, hi.Z),
        };
        int[,] edges =
        {
            {0,1},{1,2},{2,3},{3,0},{4,5},{5,6},{6,7},{7,4},{0,4},{1,5},{2,6},{3,7},
        };
        for (int i = 0; i < 12; i++) DrawWorldLine(corners[edges[i, 0]], corners[edges[i, 1]], c);
    }

    private void DrawWorldLine(Vector3 a, Vector3 b, Color color)
    {
        if (!ProjectToScreenInspector(a, out var sa) || !ProjectToScreenInspector(b, out var sb)) return;
        float dx = sb.X - sa.X, dy = sb.Y - sa.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) return;
        float ang = MathF.Atan2(dy, dx);
        _spriteBatch.Draw(_whitePixel, new Rectangle((int)sa.X, (int)sa.Y, (int)len, 2), null, color, ang,
            new Vector2(0f, 1f), SpriteEffects.None, 0f);
    }

    private bool ProjectToScreenInspector(Vector3 world, out Vector2 screen)
    {
        screen = default;
        var clip = Vector4.Transform(new Vector4(world.X, world.Y, world.Z, 1f), _inspectDebugViewProj);
        if (clip.W <= 1e-6f) return false;
        var vp = GraphicsDevice.Viewport;
        screen = new Vector2((clip.X / clip.W * 0.5f + 0.5f) * vp.Width, (0.5f - clip.Y / clip.W * 0.5f) * vp.Height);
        return true;
    }

    // ---- HUD ----

    private void DrawInspectHud(Viewport viewport)
    {
        var h = CurrentInspectHull();
        if (h is null) return;
        int w = viewport.Width;
        _spriteBatch.Begin();
        int y = 12;
        void Line(string s, Color c) { _spriteBatch.DrawString(_hudSmall, s, new Vector2(14, y), c); y += 22; }
        var list = VisibleInspectHulls();
        Line($"HULL {_inspectIndex + 1}/{list.Count}  [{h.CivKey ?? "?"}]  {h.Name}  (id {h.HullId})", Color.White);
        Line($"model={h.ModelName}  length={h.Length:0.#}  native={h.NativeAvailable}  meshy={h.MeshyAsset ?? "-"}  fallback={h.FallbackUsed}", Color.LightGray);
        Line($"cam={( _inspectCam == InspectCamMode.FlyBy ? "flyby" : "orbit" )}  yaw={_inspectOrbit.Yaw:0.##} pitch={_inspectOrbit.Pitch:0.##} dist={_inspectOrbit.Distance:0.#}  cloak={(_inspectCloak > 0f ? _inspectCloak.ToString("0.##") : "off")}", Color.LightGray);
        Line($"art={( _inspectShowNative && _inspectShowMeshy ? "both" : _inspectShowNative ? "native" : "meshy" )} side={_inspectSideBySide} wire={_inspectWireframe} norm={_inspectNormals} back={_inspectBackfaces} light={_inspectLightingVariant} soft={_inspectSoft} bloom={_inspectBloom}", Color.LightGray);
        Line($"vfx: engine={_inspectEngine} smoke={(_inspectSmoke ? _inspectHullFraction.ToString("P0") : "off")} proj={_inspectProjectiles} miss={_inspectMissiles} mines={_inspectMines} chaos={_inspectChaos}  pace={(_inspectPaused ? "PAUSED" : $"x{_inspectTimeScale:0.##}")}", Color.LightGray);
        Line($"pick: proj[{ProjectileName(_inspectProjectilePicker)}]  miss[{MissileName(_inspectMissilePicker)}]  mine[{MineName(_inspectMinePicker)}]", Color.LightGray);
        string tag = MarkTags[Math.Clamp(_inspectTagIndex, 0, MarkTags.Length - 1)];
        Line($"mark-bad: Tag=[{(tag.Length == 0 ? "(none)" : tag)}] note='{_inspectNote}'  (Tab=tag, Enter=mark, F11=shot)", tag.Length == 0 ? Color.DimGray : Color.Yellow);
        _spriteBatch.End();
    }

    private string ProjectileName(int idx)
    {
        if (_shopCatalog?.ProjectileTypes is not { } pts || pts.Count == 0) return "-";
        var keys = pts.Keys.OrderBy(k => k).ToList();
        var key = keys[Math.Clamp(idx, 0, keys.Count - 1)];
        return pts[key].ModelName;
    }
    private string MissileName(int idx)
    {
        if (_shopCatalog?.MissileTypes is not { } mts || mts.Count == 0) return "-";
        var keys = mts.Keys.OrderBy(k => k).ToList();
        var key = keys[Math.Clamp(idx, 0, keys.Count - 1)];
        return mts[key].LauncherName;
    }
    private string MineName(int idx)
    {
        if (_shopCatalog?.MineTypes is not { } mns || mns.Count == 0) return "-";
        var keys = mns.Keys.OrderBy(k => k).ToList();
        var key = keys[Math.Clamp(idx, 0, keys.Count - 1)];
        return mns[key].LauncherName;
    }

    private SectorLighting? InspectSectorLighting()
    {
        return _inspectLightingVariant switch
        {
            0 => null, // stock legacy rig (headlight + default lighting)
            1 => Allegiance.Core.Rendering.SectorLighting.FromCluster(new[] { 0.3f, -0.8f, 0.5f }, 0xFFFFFFFF),
            2 => Allegiance.Core.Rendering.SectorLighting.FromCluster(new[] { 0.1f, -0.6f, 0.3f }, 0xFF6060A0),
            _ => Allegiance.Core.Rendering.SectorLighting.FromCluster(new[] { 0.0f, -1.0f, 0.0f }, 0xFFFFE0C0),
        };
    }

    // ---- screenshot ----

    /// <summary>Reset the inspector showcase to a clean baseline (VFX off, no cloak, orbit
    /// camera framed on the current hull, sector-lit, real soft/bloom settings).</summary>
    private void InspectDemoReset()
    {
        _inspectEngine = _inspectSmoke = _inspectExplosion = _inspectProjectiles =
            _inspectMissiles = _inspectMines = _inspectChaos = false;
        _inspectCloak = 0f;
        _inspectSideBySide = false;
        _inspectShowNative = true; _inspectShowMeshy = true;
        _inspectCam = InspectCamMode.Orbit;
        _inspectFlyByHullMoves = false;
        _inspectLightingVariant = 1;
        _inspectRounds.Clear(); _inspectMissileSim.Clear(); _inspectBlasts.Clear(); _inspectMineSim.Clear();
        _inspectOrbit.Reset(0f);
        var h = CurrentInspectHull();
        if (h is not null) _inspectOrbit.FrameRadius(0.5f * (h.Length > 0f ? h.Length : 400f), DockedHullPreviewFraming.FovRadians);
    }

    /// <summary>Apply one showcase step for the --inspect-demo visual proof. Each step sets
    /// a distinct look/VFX/camera state; the current frame then screenshots it.</summary>
    private void InspectDemoApply(int step)
    {
        InspectDemoReset();
        switch (step)
        {
            case 0: _inspectShotTag = "baseline"; break;
            case 1:
                _inspectShotTag = "closeup";
                _inspectOrbit.Zoom(30);                       // wheel-zoom IN on a detail
                break;
            case 2:
                _inspectShotTag = "cloaked";
                _inspectCloak = 0.35f;
                break;
            case 3:
                _inspectShotTag = "damage_engine_explosion";  // Enhanced + soft
                _inspectSmoke = true; _inspectHullFraction = 0.25f;
                _inspectEngine = true;
                FireInspectExplosion();
                break;
            case 4:
                _inspectShotTag = "guns_missiles";
                _inspectProjectiles = true;
                for (int i = 0; i < 14; i++) FireInspectRound();
                _inspectMissiles = true;
                LaunchInspectMissiles(5);
                break;
            case 5:
                _inspectShotTag = "mines_guns";
                _inspectMines = true; FieldInspectMines(40);
                _inspectProjectiles = true;
                for (int i = 0; i < 14; i++) FireInspectRound();
                break;
            case 6:
                _inspectShotTag = "chaos";
                ToggleInspectChaos();
                break;
            case 7:
                _inspectShotTag = "sideside";
                _inspectSideBySide = true;
                break;
            case 8:
                _inspectShotTag = "flyby_approach";
                _inspectCam = InspectCamMode.FlyBy;
                _inspectFlyBy.T = 0.06f;
                break;
            case 9:
                _inspectShotTag = "flyby_close";
                _inspectCam = InspectCamMode.FlyBy;
                _inspectFlyBy.T = 0.5f;
                break;
            default:
                _inspectShotTag = "flyby_recede";
                _inspectCam = InspectCamMode.FlyBy;
                _inspectFlyBy.T = 0.94f;
                break;
        }
    }

    private string TakeInspectScreenshot()
    {
        var h = CurrentInspectHull();
        string slug = h?.Slug ?? "unknown";
        int w = GraphicsDevice.PresentationParameters.BackBufferWidth;
        int ht = GraphicsDevice.PresentationParameters.BackBufferHeight;
        var px = new Color[w * ht];
        GraphicsDevice.GetBackBufferData<Color>(px);
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "screenshots");
        Directory.CreateDirectory(dir);
        string tag = string.IsNullOrEmpty(_inspectShotTag) ? "" : _inspectShotTag + "_";
        string path = Path.Combine(dir, $"inspect_{tag}{slug}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss_fff}.png");
        System.IO.File.WriteAllBytes(path, CapturePngSaver.Encode(px, w, ht));
        Console.WriteLine($"[inspect] screenshot -> {path}");
        return path;
    }
}
