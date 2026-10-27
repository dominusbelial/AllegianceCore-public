using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P828 — the Modern-HUD radar label declutter. Pins the pure selection law
/// (priority always labelled, native-suppressed never labelled, the nearest-N
/// cap with enemies first, distance fade, deterministic overlap merge that never
/// merges the target), the per-style consumer behaviour (Classic output is
/// byte-identical to the native loop, Modern declutters), the zero-allocation
/// frame budget and the settings/menu wiring.
/// </summary>
public class P828RadarLabelDeclutterTests
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

    /// <summary>A crowded frame: 200 ships packed into a couple of battle
    /// clusters, a mix of enemies and friendlies, all within the native 1000 m
    /// label clip so the Default radar mode allows every one of them a label
    /// (native draws them all — the "excel" wall the brief opens with).</summary>
    private static HudContact[] CrowdedFrame(int count = 200, int targetIndex = 117)
    {
        var contacts = new HudContact[count];
        for (int i = 0; i < count; i++)
        {
            contacts[i] = Contact(i,
                x: 200f + (i % 30) * 60f, y: 120f + (i / 30) * 26f,
                enemy: i % 3 == 0, sameSide: i % 3 != 0,
                selected: i == targetIndex,
                cameraDistance: 200f + (i / 30) * 400f + (i % 30) * 12f);
        }
        return contacts;
    }

    // ── 1. priority selection ──────────────────────────────────────────────

    [Fact]
    public void The_selected_target_is_always_labelled()
    {
        var contacts = CrowdedFrame(count: 200, targetIndex: 117);
        var model = new RadarLabelDeclutter();
        // N = 0: no ordinary label at all, so ONLY the priority set can draw.
        model.Select(contacts, RadarMode.Default, commandCamera: false,
            RadarLabelDeclutterOptions.Modern(0, 1f));

        Assert.Equal(1, model.LabelledCount);
        Assert.True(model.IsLabelled(117));
        Assert.Equal(RadarLabelDraw.IconOnly, model.Draw(0));
    }

    [Fact]
    public void Wingmen_command_recipients_and_my_attackers_are_always_labelled()
    {
        // The native important mask (radarimage.cpp:704-710) + the chat/command
        // subject (c_maskSubject, :652) + the own ship.
        var contacts = new[]
        {
            Contact(0, enemy: true, sameSide: false, threat: true),   // locked by / shooting me
            Contact(1, subject: true),                                 // wingman / command recipient
            Contact(2, selected: true),                                // my target (locked on)
            Contact(3, isSelf: true),                                  // own ship (All mode)
            Contact(4),                                                // ordinary friendly
            Contact(5, enemy: true, sameSide: false),                  // ordinary enemy
            Contact(6),                                                // ordinary friendly
        };
        var model = new RadarLabelDeclutter();
        model.Select(contacts, RadarMode.Default, false, RadarLabelDeclutterOptions.Modern(1, 1f));

        Assert.True(model.IsLabelled(0));
        Assert.True(model.IsLabelled(1));
        Assert.True(model.IsLabelled(2));
        Assert.True(model.IsLabelled(3));
        // One ordinary slot, and the enemy wins it (enemies first).
        Assert.True(model.IsLabelled(5));
        Assert.False(model.IsLabelled(4));
        Assert.False(model.IsLabelled(6));

        // The predicate itself is pure and native-cited.
        Assert.True(RadarLabelDeclutter.IsAlwaysLabelled(contacts[2]));
        Assert.False(RadarLabelDeclutter.IsAlwaysLabelled(contacts[4]));
    }

    [Fact]
    public void A_contact_native_suppresses_is_never_labelled()
    {
        // Target radar mode: friendly ships leave the radar entirely
        // (radarimage.cpp:758-777), so even a huge N and a friendly target must
        // not resurrect a label. Important contacts ARE labelled in that mode
        // (radarimage.cpp:703-712), which is the only difference from native.
        var contacts = new[]
        {
            Contact(0, sameSide: true),                                  // friendly: native suppresses
            Contact(1, sameSide: true, selected: true),                  // important: native labels
            Contact(2, enemy: true, sameSide: false),                    // enemy: native labels
        };
        var model = new RadarLabelDeclutter();
        var huge = RadarLabelDeclutterOptions.Modern(RadarLabelDeclutter.MaxExtraLabelsLimit, 1f);
        model.Select(contacts, RadarMode.Target, false, huge);

        Assert.False(model.IsLabelled(0));
        Assert.Equal(RadarLabelDraw.IconOnly, model.Draw(0));
        Assert.True(model.IsLabelled(1));
        Assert.True(model.IsLabelled(2));

        // Minimal suppresses every non-self label (radarimage.cpp:724-731).
        var minimal = new RadarLabelDeclutter();
        minimal.Select(contacts, RadarMode.Minimal, false, huge);
        Assert.False(minimal.IsLabelled(0));
        Assert.False(minimal.IsLabelled(2));   // not important → no label in Minimal
        Assert.True(minimal.IsLabelled(1));    // important mask beats the LOD

        // A friendly ship with no name and no stat tier has nothing to draw at
        // all in Default mode: the selector must not invent a label.
        var bare = new[] { Contact(3, sameSide: true, clip: 400f) with { Name = null, Shield = null, Hull = null } };
        var bareModel = new RadarLabelDeclutter();
        bareModel.Select(bare, RadarMode.Default, false, RadarLabelDeclutterOptions.Modern(12, 1f));
        Assert.False(bareModel.IsLabelled(0));
    }

    // ── 2. the nearest-N cap with enemies first ────────────────────────────

    [Fact]
    public void The_nearest_N_cap_limits_only_the_ordinary_labels()
    {
        var contacts = CrowdedFrame(count: 120, targetIndex: 60);
        var model = new RadarLabelDeclutter();
        model.Select(contacts, RadarMode.Default, false, RadarLabelDeclutterOptions.Modern(12, 1f));

        // 12 ordinary labels + the one priority (the selected target, which is
        // not in the 12 nearest by construction below).
        Assert.True(model.LabelledCount > 0);
        Assert.True(model.LabelledCount <= 13, $"labelled {model.LabelledCount}");
        Assert.True(model.IsLabelled(60));
        int iconOnly = 0;
        for (int i = 0; i < contacts.Length; i++)
            if (model.Draw(i) == RadarLabelDraw.IconOnly) iconOnly++;
        Assert.Equal(contacts.Length - model.LabelledCount - model.MergedCount, iconOnly);

        // N = 0 draws only priority contacts.
        var none = new RadarLabelDeclutter();
        none.Select(contacts, RadarMode.Default, false, RadarLabelDeclutterOptions.Modern(0, 1f));
        Assert.Equal(1, none.LabelledCount);
    }

    [Fact]
    public void Enemies_come_before_friendlies_in_the_N_cap()
    {
        // Ten friendly ships much closer than ten enemies: the 4 ordinary slots
        // still go to the ENEMIES (the brief's ordering). Spread wide so each
        // label lands in its own merge cell (this pins the N cap, not merging).
        var contacts = new HudContact[20];
        for (int i = 0; i < 10; i++)
            contacts[i] = Contact(i, x: 100f + i * 200f, y: 100f, cameraDistance: 100f);
        for (int i = 0; i < 10; i++)
            contacts[10 + i] = Contact(10 + i, x: 100f + i * 200f, y: 800f,
                enemy: true, sameSide: false, cameraDistance: 3500f);

        var model = new RadarLabelDeclutter();
        model.Select(contacts, RadarMode.Default, false, RadarLabelDeclutterOptions.Modern(4, 1f));

        Assert.True(model.IsLabelled(10));
        for (int i = 11; i < 14; i++) Assert.NotEqual(RadarLabelDraw.IconOnly, model.Draw(i));
        for (int i = 14; i < 20; i++) Assert.Equal(RadarLabelDraw.IconOnly, model.Draw(i));
        for (int i = 0; i < 10; i++) Assert.Equal(RadarLabelDraw.IconOnly, model.Draw(i));
    }

    [Fact]
    public void Ships_outrank_rocks_and_treasure_range_lines_in_the_N_cap()
    {
        // A crowded sector is mostly ASTEROIDS whose native Default tier is a bare
        // range line (radarimage.cpp:922-935) — the bulk of the "excel" wall. They
        // must not eat the N budget: the ships win the slots even when the rocks
        // are much nearer.
        var contacts = new List<HudContact>();
        for (int i = 0; i < 20; i++)
            contacts.Add(new HudContact(HudContactKind.Asteroid, new HudPoint(100f + i * 30f, 100f),
                true, 1f, null, false, false, Name: "A" + i, Range: 200,
                ClipSeparation: 100f, CameraDistance: 150f));
        for (int i = 0; i < 5; i++)
            contacts.Add(Contact(100 + i, x: 100f + i * 200f, y: 600f, cameraDistance: 2500f));

        var model = new RadarLabelDeclutter();
        model.Select(contacts, RadarMode.Default, false, RadarLabelDeclutterOptions.Modern(5, 1f));

        for (int i = 0; i < 20; i++) Assert.Equal(RadarLabelDraw.IconOnly, model.Draw(i));
        for (int i = 20; i < 25; i++) Assert.NotEqual(RadarLabelDraw.IconOnly, model.Draw(i));
    }

    [Fact]
    public void Distance_breaks_ties_inside_one_side_and_keeps_contact_order_as_the_last_key()
    {
        var contacts = new[]
        {
            Contact(0, x: 100f, y: 100f, cameraDistance: 900f),
            Contact(1, x: 200f, y: 100f, cameraDistance: 300f),
            Contact(2, x: 300f, y: 100f, cameraDistance: 300f),
            Contact(3, x: 400f, y: 100f, cameraDistance: 600f),
        };
        var model = new RadarLabelDeclutter();
        model.Select(contacts, RadarMode.Default, false, RadarLabelDeclutterOptions.Modern(3, 1f));
        // Nearest 3: index 1 and 2 (300 m, order kept), then index 3 (600 m).
        Assert.True(model.IsLabelled(1));
        Assert.True(model.IsLabelled(2));
        Assert.True(model.IsLabelled(3));
        Assert.False(model.IsLabelled(0));
    }

    // ── 3. distance fade ──────────────────────────────────────────────────

    [Fact]
    public void Fade_near_faints_from_native_ceiling_far_to_floor_and_is_monotonic()
    {
        const float near = RadarLabelDeclutter.DefaultFadeNear;
        const float far = RadarLabelDeclutter.DefaultFadeFar;
        const float floor = RadarLabelDeclutter.DefaultMinAlpha;
        const float ceiling = RadarLabelDeclutter.NativeNearAlpha;

        Assert.Equal(ceiling, RadarLabelDeclutter.Fade(0f, near, far, floor, ceiling));
        Assert.Equal(ceiling, RadarLabelDeclutter.Fade(near, near, far, floor, ceiling));
        Assert.Equal(floor, RadarLabelDeclutter.Fade(far, near, far, floor, ceiling), 5);
        Assert.Equal(floor, RadarLabelDeclutter.Fade(far * 1000f, near, far, floor, ceiling), 5);

        float previous = ceiling;
        for (float d = 0f; d <= far * 1.5f; d += 50f)
        {
            float alpha = RadarLabelDeclutter.Fade(d, near, far, floor, ceiling);
            Assert.True(alpha <= previous + 1e-6f, $"fade rose at {d}: {alpha} > {previous}");
            Assert.InRange(alpha, floor, ceiling);
            previous = alpha;
        }
        // Halfway is strictly between (a real ramp, not a step).
        float mid = RadarLabelDeclutter.Fade((near + far) / 2f, near, far, floor, ceiling);
        Assert.True(mid < ceiling && mid > floor);

        // A far contact's label is fainter than a near one's; the near one is
        // native's 0.75 closeAlpha (P836b — the Modern fade honours the ceiling).
        var nearContact = Contact(0, x: 300f, y: 200f, cameraDistance: 250f);
        var farContact = Contact(1, x: 900f, y: 200f, cameraDistance: far);
        var model = new RadarLabelDeclutter();
        model.Select(new[] { nearContact, farContact }, RadarMode.Default, false,
            RadarLabelDeclutterOptions.Modern(12, 1f));
        Assert.Equal(ceiling, model.Alpha(0), 5);
        Assert.Equal(floor, model.Alpha(1), 5);
    }

    // ── 4. overlap merge ──────────────────────────────────────────────────

    [Fact]
    public void Overlapping_labels_collapse_into_one_count_tag_and_the_target_never_merges()
    {
        // Five ordinary contacts in one 120x46 px grid cell (screen space) plus
        // the target in the same cell: the ordinary five become ONE tag, the
        // target keeps its own label.
        var contacts = new[]
        {
            Contact(0, x: 400f, y: 300f), Contact(1, x: 405f, y: 302f),
            Contact(2, x: 410f, y: 305f), Contact(3, x: 415f, y: 308f),
            Contact(4, x: 420f, y: 310f),
            Contact(5, x: 402f, y: 303f, selected: true),
        };
        var model = new RadarLabelDeclutter();
        model.Select(contacts, RadarMode.Default, false, RadarLabelDeclutterOptions.Modern(12, 1f));

        Assert.Equal(5, model.MergedCount);
        Assert.Equal(1, model.TagCount);
        var tag = model.Tags[0];
        Assert.Equal(5, tag.Count);
        Assert.False(tag.Enemy);
        Assert.InRange(tag.Point.X, 400f, 420f);
        Assert.InRange(tag.Point.Y, 300f, 310f);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(RadarLabelDraw.Merged, model.Draw(i));
            Assert.False(model.IsLabelled(i));
        }
        Assert.True(model.IsLabelled(5));
        Assert.Equal("x5", RadarLabelDeclutter.CountTag(tag.Count));

        // Deterministic: the same input produces the same tags, run twice.
        var again = new RadarLabelDeclutter();
        again.Select(contacts, RadarMode.Default, false, RadarLabelDeclutterOptions.Modern(12, 1f));
        Assert.Equal(model.TagCount, again.TagCount);
        Assert.Equal(model.Tags[0], again.Tags[0]);
        Assert.Equal(1, model.LabelledCount);
    }

    [Fact]
    public void A_cell_with_both_sides_produces_two_tags_one_per_side()
    {
        var contacts = new[]
        {
            Contact(0, x: 400f, y: 300f), Contact(1, x: 405f, y: 302f),
            Contact(2, x: 408f, y: 306f, enemy: true, sameSide: false),
            Contact(3, x: 412f, y: 308f, enemy: true, sameSide: false),
        };
        var model = new RadarLabelDeclutter();
        model.Select(contacts, RadarMode.Default, false, RadarLabelDeclutterOptions.Modern(12, 1f));

        Assert.Equal(2, model.TagCount);
        Assert.Contains(model.Tags, t => t is { Count: 2, Enemy: true });
        Assert.Contains(model.Tags, t => t is { Count: 2, Enemy: false });
        Assert.Equal(4, model.MergedCount);
    }

    [Fact]
    public void A_lone_label_in_a_cell_keeps_its_block()
    {
        var contacts = new[]
        {
            Contact(0, x: 400f, y: 300f),
            Contact(1, x: 1400f, y: 900f),
        };
        var model = new RadarLabelDeclutter();
        model.Select(contacts, RadarMode.Default, false, RadarLabelDeclutterOptions.Modern(12, 1f));
        Assert.Equal(0, model.TagCount);
        Assert.Equal(0, model.MergedCount);
        Assert.True(model.IsLabelled(0));
        Assert.True(model.IsLabelled(1));
    }

    // ── 5. Classic == native, Modern == decluttered (the consumer contract) ──

    private static RadarLabelDeclutterOptions PlanOf(object renderer, Type type)
    {
        var property = type.GetProperty("RadarDeclutterPlan",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(type.Name, "RadarDeclutterPlan");
        return (RadarLabelDeclutterOptions)property.GetValue(renderer)!;
    }

    [Fact]
    public void Classic_draws_every_native_label_and_modern_declutters_it()
    {
        var contacts = CrowdedFrame(count: 200, targetIndex: 117);

        // Classic's own policy (the HudRenderer base the classic skin uses):
        // every label the native Default tier allows, full opacity, no tags.
        var classicRenderer = (HudRenderer)RuntimeHelpers.GetUninitializedObject(typeof(HudRenderer));
        var classicPlan = PlanOf(classicRenderer, typeof(HudRenderer));
        Assert.False(classicPlan.Enabled);

        int allowed = 0;
        for (int i = 0; i < contacts.Length; i++)
            if (RadarLabelModel.Resolve(contacts[i], RadarMode.Default, commandCamera: false) is not null) allowed++;
        Assert.Equal(contacts.Length, allowed);   // native labels every one of them

        var classic = new RadarLabelDeclutter();
        classic.Select(contacts, RadarMode.Default, false, classicPlan);
        Assert.Equal(allowed, classic.LabelledCount);
        Assert.Equal(0, classic.MergedCount);
        Assert.Equal(0, classic.TagCount);
        for (int i = 0; i < contacts.Length; i++)
        {
            Assert.True(classic.IsLabelled(i), $"native label {i} must still draw in Classic");
            Assert.Equal(1f, classic.Alpha(i));
        }

        // Modern's policy: same frame, decluttered.
        var modern = (ModernHudRenderer)RuntimeHelpers.GetUninitializedObject(typeof(ModernHudRenderer));
        GC.SuppressFinalize(modern);
        var settings = new HudSettings { Style = HudStyle.Modern };
        typeof(ModernHudRenderer).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(modern, settings);
        typeof(ModernHudRenderer).GetField("_scale", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(modern, 1f);
        var modernPlan = PlanOf(modern, typeof(ModernHudRenderer));
        Assert.True(modernPlan.Enabled);
        Assert.Equal(RadarLabelDeclutter.DefaultExtraLabels, modernPlan.MaxExtraLabels);

        var decluttered = new RadarLabelDeclutter();
        decluttered.Select(contacts, RadarMode.Default, false, modernPlan);
        Assert.True(decluttered.LabelledCount < allowed / 4,
            $"Modern still draws {decluttered.LabelledCount} of {allowed} labels");
        Assert.True(decluttered.IsLabelled(117));   // the target survives both

        // A dense friendly cluster (the nearest-N picks land in a few grid
        // cells) collapses into count tags instead of a wall of names.
        var cluster = new HudContact[40];
        for (int i = 0; i < 40; i++)
            cluster[i] = Contact(i, x: 400f + (i % 6) * 20f, y: 300f + (i / 6) * 10f,
                cameraDistance: 200f + i * 10f);
        decluttered.Select(cluster, RadarMode.Default, false, modernPlan);
        Assert.True(decluttered.MergedCount > 0, "a packed cluster must merge");
        Assert.True(decluttered.TagCount > 0);
        Assert.True(decluttered.LabelledCount < 40);

        // The player's explicit override wins in either style.
        settings.RadarDeclutter = false;
        Assert.False(PlanOf(modern, typeof(ModernHudRenderer)).Enabled);
        var classicWithOption = new HudSettings { Style = HudStyle.Classic, RadarDeclutter = true };
        Assert.True(classicWithOption.RadarLabelPlan(1f).Enabled);
    }

    // ── 6. frame budget ───────────────────────────────────────────────────

    [Fact]
    public void The_selection_path_allocates_nothing_per_frame()
    {
        var contacts = CrowdedFrame(count: 400);
        var model = new RadarLabelDeclutter();
        var cache = new RadarLabelCache();
        var plan = RadarLabelDeclutterOptions.Modern(12, 1f);
        for (int warm = 0; warm < 20; warm++)
            model.Select(contacts, RadarMode.Default, false, plan, cache);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 120; frame++)
            model.Select(contacts, RadarMode.Default, false, plan, cache);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // 400 contacts, 120 frames: not one byte (decision/scratch arrays, the
        // cluster and tag lists and the count-tag strings are all reused).
        Assert.Equal(0, allocated);
        Assert.Equal(400, model.ContactCount);

        // The option-off path is allocation-free too (Classic's cost).
        var off = RadarLabelDeclutterOptions.Off;
        for (int warm = 0; warm < 20; warm++) model.Select(contacts, RadarMode.Default, false, off, cache);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 120; frame++) model.Select(contacts, RadarMode.Default, false, off, cache);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ── 7. settings + menu wiring ─────────────────────────────────────────

    [Fact]
    public void The_declutter_setting_defaults_per_style_persists_and_clamps()
    {
        // Nullable like HudSettings.SelectionBracket: null = the style default.
        Assert.True(new HudSettings { Style = HudStyle.Modern }.RadarDeclutterOn);
        Assert.False(new HudSettings { Style = HudStyle.Classic }.RadarDeclutterOn);
        Assert.False(new HudSettings { Style = HudStyle.Modern, RadarDeclutter = false }.RadarDeclutterOn);
        Assert.True(new HudSettings { Style = HudStyle.Classic, RadarDeclutter = true }.RadarDeclutterOn);

        var settings = new HudSettings { Style = HudStyle.Modern, RadarLabelMax = 999, RadarLabelTelemetry = true };
        string path = Path.Combine(Path.GetTempPath(), "p828-hud-" + Guid.NewGuid(), "hud.json");
        try
        {
            settings.Save(path);
            var loaded = HudSettings.Load(path, existingPlayer: true);
            Assert.True(loaded.RadarDeclutterOn);
            Assert.Null(loaded.RadarDeclutter);
            Assert.Equal(RadarLabelDeclutter.MaxExtraLabelsLimit, loaded.RadarLabelMax);
            Assert.True(loaded.RadarLabelTelemetry);
            Assert.True(loaded.RadarLabelPlan(1f).Telemetry);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true);
        }

        // The Ctrl+F6 options panel carries the three rows and toggles them.
        string gpu = SourcePath("Game1.GpuBudget.cs");
        Assert.Contains("Radar label declutter: ", gpu);
        Assert.Contains("Radar labels kept: ", gpu);
        Assert.Contains("Radar label telemetry: ", gpu);
        Assert.Contains("case 16: _hudSettings.RadarDeclutter", gpu);
        Assert.Contains("case 17: _hudSettings.RadarLabelMax", gpu);
        Assert.Contains("case 18: _hudSettings.RadarLabelTelemetry", gpu);
        Assert.Contains("GraphicsRowCount", gpu);
        Assert.True(Game1.GraphicsPanelHeight(19) > 700,
            "the panel must be tall enough for the three new rows");
    }

    private static string SourcePath(string file)
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        return File.ReadAllText(Path.Combine(root, "src", "Allegiance.ClientCore.Game", file));
    }

    /// <summary>The Modern declutter fades with native's commented-out curve
    /// (radarimage.cpp:686-700): closeAlpha 0.75 near, farAlpha 0.25 far, linear;
    /// the native 100/1000 m pair reproduces it exactly.</summary>
    [Fact]
    public void Modern_fade_uses_native_close_and_far_alpha()
    {
        var modern = RadarLabelDeclutterOptions.Modern(12, 1f);
        Assert.Equal(.75f, modern.MaxAlpha);
        Assert.Equal(.25f, modern.MinAlpha);
        const float near = RadarLabelDeclutter.NativeFadeNear, far = RadarLabelDeclutter.NativeFadeFar;
        float Native(float d) => RadarLabelDeclutter.Fade(d, near, far, RadarLabelDeclutter.NativeFarAlpha, RadarLabelDeclutter.NativeNearAlpha);
        Assert.Equal(.75f, Native(50f), 5);
        Assert.Equal(.5f, Native(550f), 5);    // native: farAlpha + (range-far)*(close-far)/(close-far range)
        Assert.Equal(.25f, Native(5000f), 5);
        Assert.Equal(1f, RadarLabelDeclutterOptions.Off.MaxAlpha);
    }
}
