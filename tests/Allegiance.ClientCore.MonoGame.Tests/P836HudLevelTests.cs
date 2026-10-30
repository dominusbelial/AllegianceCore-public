using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P836 — Modern-HUD density levels (Full / Tactical / Combat / Cinematic /
/// Auto), built on the P828 declutter. Pins: each level's label set for a fixed
/// crowded scene, the Auto thresholds + hysteresis (no flicker over a scripted
/// ship-count series), the per-level off-screen rule, the settings round-trip,
/// the cycle-key/options wiring, and that Classic stays byte-identical to master
/// (ignores the level entirely).
/// </summary>
public class P836HudLevelTests
{
    // ── fixtures ───────────────────────────────────────────────────────────

    private static HudContact Contact(
        int index, float x = 0f, float y = 0f, bool enemy = false, bool sameSide = true,
        bool selected = false, bool subject = false, bool threat = false, bool isSelf = false,
        string? name = null, float? cameraDistance = 500f, float? clip = 400f, int? range = 500,
        bool onScreen = true)
        => new(HudContactKind.Ship, new HudPoint(x, y), onScreen, 1f, null, selected, false,
            Name: name ?? "mcp-w1-b" + index + "-" + index * 1000,
            Range: range, Shield: .8f, Hull: .9f,
            IsEnemy: enemy, IsSelf: isSelf, IsSameSide: sameSide,
            Subject: subject, Threat: threat,
            ClipSeparation: clip, CameraDistance: cameraDistance);

    /// <summary>A crowded frame: 200 ships packed into battle clusters, a mix of
    /// enemies and friendlies, all within the native 1000 m label clip so the
    /// Default radar mode allows every one a label (native draws them all).</summary>
    private static HudContact[] CrowdedFrame(int count = 200, int targetIndex = 117)
    {
        var contacts = new HudContact[count];
        for (int i = 0; i < count; i++)
            contacts[i] = Contact(i,
                x: 200f + (i % 30) * 60f, y: 120f + (i / 30) * 26f,
                enemy: i % 3 == 0, sameSide: i % 3 != 0, selected: i == targetIndex,
                cameraDistance: 200f + (i / 30) * 400f + (i % 30) * 12f);
        return contacts;
    }

    private static RadarLabelDeclutterOptions Plan(HudLevel level, float scale = 1f, int extra = 12)
        => RadarLabelDeclutterOptions.FromPreset(HudLevels.Preset(level), scale, extra);

    // ── 1. each level's label set on the SAME crowded scene ────────────────

    [Fact]
    public void Full_labels_every_allowed_contact_with_distance_fade()
    {
        var contacts = CrowdedFrame(count: 200, targetIndex: 117);
        var model = new RadarLabelDeclutter();
        model.Select(contacts, RadarMode.Default, false, Plan(HudLevel.Full));

        // Full = the native loop: every native-allowed label, no cap, no merge.
        Assert.Equal(200, model.LabelledCount);
        Assert.Equal(0, model.MergedCount);
        Assert.Equal(0, model.TagCount);
        // The fade is applied (native shape; the near ceiling is native's 0.75
        // closeAlpha — P836b honours the ceiling, radarimage.cpp:686-700): a far
        // contact's label is fainter than a near one's, and the fixture's nearest
        // (~200 m) is already past the 100 m near point so it is below the ceiling.
        Assert.True(model.Alpha(0) < 1f, "Full fades, it is not the flat native loop");
        Assert.True(model.Alpha(contacts.Length - 1) < model.Alpha(0), "a farther label must be fainter");
        Assert.InRange(model.Alpha(contacts.Length - 1), RadarLabelDeclutter.DefaultMinAlpha, RadarLabelDeclutter.NativeNearAlpha);

        // Full uses the NATIVE fade pair (100/1000).
        Assert.Equal(RadarLabelDeclutter.NativeFadeNear, HudLevels.Preset(HudLevel.Full).FadeNear);
        Assert.Equal(RadarLabelDeclutter.NativeFadeFar, HudLevels.Preset(HudLevel.Full).FadeFar);
    }

    [Fact]
    public void Tactical_keeps_priority_plus_nearest_N_with_merge_tags()
    {
        // A dense friendly cluster collapses into count tags (the P828 merge),
        // plus the priority target always survives.
        var cluster = new HudContact[40];
        for (int i = 0; i < 40; i++)
            cluster[i] = Contact(i, x: 400f + (i % 6) * 20f, y: 300f + (i / 6) * 10f,
                cameraDistance: 200f + i * 10f);
        var model = new RadarLabelDeclutter();
        model.Select(cluster, RadarMode.Default, false, Plan(HudLevel.Tactical));
        Assert.True(model.MergedCount > 0, "a packed cluster must merge in Tactical");
        Assert.True(model.TagCount > 0);

        // On the crowded 200-ship frame: the selected target always draws, plus
        // at most the 12 nearest-N.
        var contacts = CrowdedFrame(count: 200, targetIndex: 117);
        var frame = new RadarLabelDeclutter();
        frame.Select(contacts, RadarMode.Default, false, Plan(HudLevel.Tactical));
        Assert.True(frame.IsLabelled(117));
        Assert.True(frame.LabelledCount > 0 && frame.LabelledCount <= 13, $"labelled {frame.LabelledCount}");

        // Tactical uses native 100/1000 fade per the brief.
        Assert.Equal(RadarLabelDeclutter.NativeFadeNear, HudLevels.Preset(HudLevel.Tactical).FadeNear);
        Assert.Equal(RadarLabelDeclutter.NativeFadeFar, HudLevels.Preset(HudLevel.Tactical).FadeFar);
    }

    [Fact]
    public void Combat_labels_enemies_and_priority_but_friendlies_are_icon_only()
    {
        // Ten enemies and ten friendlies, friendlies MUCH nearer so any ordinary
        // slot would naturally go to them — Combat must still refuse them.
        var contacts = new HudContact[20];
        for (int i = 0; i < 10; i++)
            contacts[i] = Contact(i, x: 100f + i * 300f, y: 100f, sameSide: true, cameraDistance: 100f);
        for (int i = 0; i < 10; i++)
            contacts[10 + i] = Contact(10 + i, x: 100f + i * 300f, y: 900f,
                enemy: true, sameSide: false, cameraDistance: 2500f);

        var model = new RadarLabelDeclutter();
        model.Select(contacts, RadarMode.Default, false, Plan(HudLevel.Combat));

        // Friendlies are icon-only (never ordinary labels)…
        for (int i = 0; i < 10; i++) Assert.Equal(RadarLabelDraw.IconOnly, model.Draw(i));
        // …but enemies take the nearest-N slots.
        Assert.True(model.LabelledCount >= 1, "enemies must be labelled in Combat");
        for (int i = 10; i < 20; i++)
            Assert.NotEqual(RadarLabelDraw.IconOnly, model.Draw(i));

        // Tightened 50/600 fade.
        Assert.Equal(HudLevels.CombatFadeNear, HudLevels.Preset(HudLevel.Combat).FadeNear);
        Assert.Equal(HudLevels.CombatFadeFar, HudLevels.Preset(HudLevel.Combat).FadeFar);

        // A PRIORITY friendly (the selected target / my attacker) still draws.
        var threat = Contact(0, enemy: true, sameSide: false, threat: true);
        var priority = new RadarLabelDeclutter();
        priority.Select(new[] { Contact(1, sameSide: true, selected: true), threat },
            RadarMode.Default, false, Plan(HudLevel.Combat));
        Assert.True(priority.IsLabelled(0));
        Assert.True(priority.IsLabelled(1));
    }

    [Fact]
    public void Cinematic_labels_only_the_selected_target()
    {
        var contacts = new[]
        {
            Contact(0, selected: true),
            Contact(1, subject: true),          // wingman — hidden in Cinematic
            Contact(2, enemy: true, sameSide: false, threat: true),  // attacker — hidden
            Contact(3),                          // ordinary friendly — hidden
        };
        var model = new RadarLabelDeclutter();
        model.Select(contacts, RadarMode.Default, false, Plan(HudLevel.Cinematic));

        Assert.True(model.IsLabelled(0));          // the target
        for (int i = 1; i < contacts.Length; i++)
            Assert.Equal(RadarLabelDraw.IconOnly, model.Draw(i));
        Assert.Equal(0, model.MergedCount);
        Assert.Equal(0, model.TagCount);
        Assert.Equal(OffscreenRule.None, HudLevels.Preset(HudLevel.Cinematic).Offscreen);
    }

    // ── 2. Auto thresholds + hysteresis, no flicker ────────────────────────

    [Fact]
    public void Auto_bands_map_ship_counts_to_concrete_levels()
    {
        Assert.Equal(HudLevel.Full, HudAutoLevel.Band(0));
        Assert.Equal(HudLevel.Full, HudAutoLevel.Band(HudAutoLevel.FullMaxShips));
        Assert.Equal(HudLevel.Tactical, HudAutoLevel.Band(HudAutoLevel.FullMaxShips + 1));
        Assert.Equal(HudLevel.Tactical, HudAutoLevel.Band(HudAutoLevel.TacticalMaxShips));
        Assert.Equal(HudLevel.Combat, HudAutoLevel.Band(HudAutoLevel.CombatMinShips));
        Assert.Equal(HudLevel.Combat, HudAutoLevel.Band(10000));
    }

    [Fact]
    public void Auto_first_sample_applies_immediately_then_holds_before_switching()
    {
        var auto = new HudAutoLevel();
        // First sample: Full (10 ships). Immediate, no hysteresis wait.
        Assert.True(auto.Advance(0.0, 10));
        Assert.Equal(HudLevel.Full, auto.Level);

        // Jump to 300 ships: Combat becomes PENDING but must not apply yet.
        Assert.False(auto.Advance(1.0, 300));
        Assert.Equal(HudLevel.Full, auto.Level);   // no flicker before the hold elapses
        Assert.False(auto.Advance(2.0, 300));
        Assert.Equal(HudLevel.Full, auto.Level);

        // After the 3 s hold with the count still high, it flips to Combat.
        Assert.True(auto.Advance(4.1, 300));
        Assert.Equal(HudLevel.Combat, auto.Level);
    }

    [Fact]
    public void Auto_never_flickers_on_an_oscillating_series_under_the_hold()
    {
        var auto = new HudAutoLevel();
        auto.Advance(0.0, 10);                       // -> Full
        // Oscillate 300 ↔ 10 every half second for 10 s; each swing restarts the
        // pending hold, so the level must stay Full the whole time.
        double t = 0.5;
        for (int i = 0; i < 20; i++)
        {
            bool a = auto.Advance(t, 300); t += 0.5;
            bool b = auto.Advance(t, 10);  t += 0.5;
            Assert.False(a || b, $"flickered at t={t:0.0}");
            Assert.Equal(HudLevel.Full, auto.Level);
        }
    }

    [Fact]
    public void Auto_holds_then_relaxes_when_the_crowd_leaves()
    {
        var auto = new HudAutoLevel();
        auto.Advance(0.0, 300);                       // -> Combat (immediate)
        Assert.Equal(HudLevel.Combat, auto.Level);
        // Drop to 5 ships; Tactical/Full band is pending for the hold, not instant.
        Assert.False(auto.Advance(1.0, 5));
        Assert.Equal(HudLevel.Combat, auto.Level);
        Assert.True(auto.Advance(4.2, 5));            // hold elapsed -> Full
        Assert.Equal(HudLevel.Full, auto.Level);
    }

    // ── 3. off-screen rule per level ───────────────────────────────────────

    [Fact]
    public void Offscreen_rule_decides_edge_blocks_purely()
    {
        var onScreen = Contact(0, onScreen: true);
        var offScreenOrdinary = Contact(1, onScreen: false);
        var offScreenPriority = Contact(2, onScreen: false, selected: true);

        // On-screen contacts are never suppressed.
        Assert.False(RadarBracketRenderer.OffscreenHidden(onScreen, OffscreenRule.NativeBlock));
        Assert.False(RadarBracketRenderer.OffscreenHidden(onScreen, OffscreenRule.EdgeDot));
        Assert.False(RadarBracketRenderer.OffscreenHidden(onScreen, OffscreenRule.None));

        // NativeBlock keeps every off-screen block (native behaviour).
        Assert.False(RadarBracketRenderer.OffscreenHidden(offScreenOrdinary, OffscreenRule.NativeBlock));
        // EdgeDot / None shrink ordinary off-screen blocks…
        Assert.True(RadarBracketRenderer.OffscreenHidden(offScreenOrdinary, OffscreenRule.EdgeDot));
        Assert.True(RadarBracketRenderer.OffscreenHidden(offScreenOrdinary, OffscreenRule.None));
        // …but a priority contact always keeps its native block.
        Assert.False(RadarBracketRenderer.OffscreenHidden(offScreenPriority, OffscreenRule.EdgeDot));
        Assert.False(RadarBracketRenderer.OffscreenHidden(offScreenPriority, OffscreenRule.None));
    }

    [Fact]
    public void EdgeDot_suppresses_ordinary_offscreen_labels_but_keeps_priority()
    {
        var contacts = new[]
        {
            Contact(0, x: 960f, y: 5f, onScreen: false),              // ordinary, edge-clamped
            Contact(1, x: 970f, y: 5f, onScreen: false, selected: true),  // priority, edge-clamped
            Contact(2, x: 975f, y: 990f, onScreen: false),            // ordinary, edge-clamped
            Contact(3, x: 400f, y: 300f, onScreen: true),             // ordinary, on-screen
        };
        var model = new RadarLabelDeclutter();
        model.Select(contacts, RadarMode.Default, false, Plan(HudLevel.Tactical)); // EdgeDot

        Assert.Equal(RadarLabelDraw.IconOnly, model.Draw(0));   // off-screen ordinary -> no label
        Assert.True(model.IsLabelled(1));                       // off-screen priority keeps its block
        Assert.Equal(RadarLabelDraw.IconOnly, model.Draw(2));   // off-screen ordinary -> no label
        Assert.True(model.IsLabelled(3));                       // on-screen ordinary labels normally
    }

    [Fact]
    public void None_drops_offscreen_blocks_entirely()
    {
        var contacts = new[]
        {
            Contact(0, onScreen: false),                          // ordinary, edge-clamped
            Contact(1, onScreen: false, selected: true),          // priority
            Contact(2, onScreen: true, selected: false),          // on-screen ordinary
        };
        var model = new RadarLabelDeclutter();
        model.Select(contacts, RadarMode.Default, false, Plan(HudLevel.Cinematic)); // OnlyTarget + None

        Assert.Equal(RadarLabelDraw.IconOnly, model.Draw(0));
        Assert.True(model.IsLabelled(1));    // the target keeps its label even off-screen
        Assert.Equal(RadarLabelDraw.IconOnly, model.Draw(2));    // Cinematic: only the target
    }

    // ── 4. settings round-trip + persistence ───────────────────────────────

    [Fact]
    public void The_level_persists_in_hud_json_and_normalizes()
    {
        foreach (var level in new[] { HudLevel.Full, HudLevel.Combat, HudLevel.Auto, HudLevel.Cinematic })
        {
            var settings = new HudSettings { Style = HudStyle.Modern, Level = level };
            string path = Path.Combine(Path.GetTempPath(), "p836-hud-" + Guid.NewGuid(), "hud.json");
            try
            {
                settings.Save(path);
                var loaded = HudSettings.Load(path, existingPlayer: true);
                Assert.Equal(level, loaded.Level);
            }
            finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }

        // Unknown persisted value clamps to a valid member; ActiveLevel is
        // runtime-only (never Auto after Normalize).
        var bad = new HudSettings { Level = (HudLevel)99 };
        bad.Normalize();
        Assert.True(Enum.IsDefined(bad.Level));
        var auto = new HudSettings { Level = HudLevel.Auto, ActiveLevel = HudLevel.Auto };
        auto.Normalize();
        Assert.Equal(HudLevel.Auto, auto.Level);
        Assert.NotEqual(HudLevel.Auto, auto.ActiveLevel);
    }

    // ── 5. the plan honours the level; Classic ignores it ──────────────────

    [Fact]
    public void RadarLabelPlan_reflects_the_active_level_and_disables_in_classic()
    {
        // Combat: no friendly ordinary labels, tightened fade.
        var combat = new HudSettings { Style = HudStyle.Modern, Level = HudLevel.Combat };
        var cp = combat.RadarLabelPlan(1f);
        Assert.True(cp.Enabled);
        Assert.False(cp.LabelFriendlies);
        Assert.Equal(HudLevels.CombatFadeNear, cp.FadeNear);
        Assert.Equal(HudLevels.CombatFadeFar, cp.FadeFar);

        // Full: label everything (AllAllowed).
        Assert.True(new HudSettings { Style = HudStyle.Modern, Level = HudLevel.Full }.RadarLabelPlan(1f).AllAllowed);
        // Cinematic: only the target.
        Assert.True(new HudSettings { Style = HudStyle.Modern, Level = HudLevel.Cinematic }.RadarLabelPlan(1f).OnlyTarget);

        // Auto: resolves through ActiveLevel (the runtime concrete).
        var auto = new HudSettings { Style = HudStyle.Modern, Level = HudLevel.Auto, ActiveLevel = HudLevel.Combat };
        var ap = auto.RadarLabelPlan(1f);
        Assert.False(ap.LabelFriendlies);      // Combat active
        Assert.Equal(HudLevel.Combat, auto.EffectiveLevel);

        // Classic (or the declutter switched off) -> the exact native Off loop,
        // whatever the level.
        Assert.Equal(RadarLabelDeclutterOptions.Off, new HudSettings { Style = HudStyle.Classic, Level = HudLevel.Cinematic }.RadarLabelPlan(1f));
        Assert.Equal(RadarLabelDeclutterOptions.Off, new HudSettings { Style = HudStyle.Modern, Level = HudLevel.Combat, RadarDeclutter = false }.RadarLabelPlan(1f));
    }

    [Fact]
    public void The_classic_renderer_policy_stays_native_regardless_of_level()
    {
        // HudRenderer (Classic) hard-codes RadarDeclutterPlan = Off, so a level
        // can never leak into the Classic path — it stays byte-identical.
        var classicRenderer = (HudRenderer)RuntimeHelpers.GetUninitializedObject(typeof(HudRenderer));
        var classicPlan = (RadarLabelDeclutterOptions)typeof(HudRenderer)
            .GetProperty("RadarDeclutterPlan", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(classicRenderer)!;
        Assert.False(classicPlan.Enabled);
        Assert.Equal(1f, classicPlan.MaxAlpha);
        Assert.Equal(1f, classicPlan.MinAlpha);
    }

    [Fact]
    public void Classic_and_modern_run_the_same_native_label_set_when_off()
    {
        // Prove the Classic == master claim for a crowded scene: with the
        // declutter disabled the label set is exactly the native-allowed set,
        // whatever the Modern level would have asked for.
        var contacts = CrowdedFrame(count: 200, targetIndex: 117);
        var classic = new RadarLabelDeclutter();
        classic.Select(contacts, RadarMode.Default, false, RadarLabelDeclutterOptions.Off);

        int allowed = 0;
        for (int i = 0; i < contacts.Length; i++)
            if (RadarLabelModel.Resolve(contacts[i], RadarMode.Default, commandCamera: false) is not null) allowed++;
        Assert.Equal(allowed, classic.LabelledCount);
        Assert.Equal(0, classic.MergedCount);
        Assert.Equal(0, classic.TagCount);
        for (int i = 0; i < contacts.Length; i++)
        {
            Assert.True(classic.IsLabelled(i));
            Assert.Equal(1f, classic.Alpha(i));
        }
    }

    // ── 6. frame budget stays zero-allocation for the new levels ───────────

    [Fact]
    public void The_level_selection_paths_allocate_nothing_per_frame()
    {
        var contacts = CrowdedFrame(count: 300);
        foreach (var level in new[] { HudLevel.Full, HudLevel.Tactical, HudLevel.Combat, HudLevel.Cinematic })
        {
            var model = new RadarLabelDeclutter();
            var cache = new RadarLabelCache();
            var plan = Plan(level);
            for (int warm = 0; warm < 20; warm++) model.Select(contacts, RadarMode.Default, false, plan, cache);
            // Best of 5 windows: JIT tier-up under a loaded test host can land in
            // one window; a real per-frame allocation shows in all of them.
            long best = long.MaxValue;
            for (int window = 0; window < 5 && best != 0; window++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int f = 0; f < 120; f++) model.Select(contacts, RadarMode.Default, false, plan, cache);
                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            Assert.Equal(0, best);
        }
    }

    // ── 7. wiring: cycle key, options panel, level cycle order ─────────────

    [Fact]
    public void The_cycle_key_action_exists_on_a_free_key()
    {
        string catalog = SourcePath("Rendering/InputActionCatalog.cs");
        Assert.Contains("\"CycleHudLevel\"", catalog);
        Assert.Contains("K(Keys.D0)", catalog);
    }

    [Fact]
    public void The_options_panel_carries_the_level_row_and_case()
    {
        string gpu = SourcePath("Game1.GpuBudget.cs");
        Assert.Contains("HUD level: ", gpu);
        Assert.Contains("case 19: _hudSettings.Level", gpu);
        Assert.Contains("GraphicsRowCount = 22", gpu);
        Assert.True(Game1.GraphicsPanelHeight(20) > 920, "20 rows must fit the panel");
    }

    [Fact]
    public void The_level_toast_and_tag_text_is_ascii_safe()
    {
        // The bundled Modern HUD fonts carry ASCII 32-126 only (the same native
        // raster-font range P828 documented), so any non-ASCII glyph in the
        // drawn level tag/toast throws a SpriteFont resolution error at draw
        // time. P836b caught this live: the typographic arrow in the "AUTO HUD ->
        // level" toast crashed the HUD the moment any non-default level was active
        // (the fast-tier crash surfaced it here). Pin the toast strings to ASCII.
        string src = SourcePath("Rendering/ModernHudRenderer.cs");
        var toastLines = src.Split('\n').Where(l => l.Contains("AUTO HUD") || l.Contains("HUD LEVEL"));
        Assert.NotEmpty(toastLines);
        foreach (var line in toastLines)
            Assert.True(line.All(c => c <= 0x7E), $"non-ASCII char in level toast text: {line.Trim()}");
    }

    [Fact]
    public void The_level_cycle_order_wraps_through_all_five_including_auto()
    {
        Assert.Equal(HudLevel.Tactical, HudLevels.Next(HudLevel.Full));
        Assert.Equal(HudLevel.Combat, HudLevels.Next(HudLevel.Tactical));
        Assert.Equal(HudLevel.Cinematic, HudLevels.Next(HudLevel.Combat));
        Assert.Equal(HudLevel.Auto, HudLevels.Next(HudLevel.Cinematic));
        Assert.Equal(HudLevel.Full, HudLevels.Next(HudLevel.Auto));
        // Backwards through the same order.
        Assert.Equal(HudLevel.Cinematic, HudLevels.Step(HudLevel.Auto, -1));
        Assert.Equal(HudLevel.Auto, HudLevels.Step(HudLevel.Cinematic, 1));
    }

    private static string SourcePath(string file)
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        return File.ReadAllText(Path.Combine(root, "src", "Allegiance.ClientCore.Game", file));
    }
}
