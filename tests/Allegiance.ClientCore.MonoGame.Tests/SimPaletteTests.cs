using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P989 — <c>visuals.space.simPalette</c>: the FS2-Open-flavoured "sim" colour
/// grade on top of P984's linear HDR pipeline (default OFF).
/// <para>What is pinned here: the flag (dotted key, free hotkey, parse/persist/
/// <c>--visuals</c>/Options row), the DEPENDENCY on <c>space.hdrPipeline</c> (the
/// grade is a no-op without the float scene target, and the options row says so),
/// the resolved grade values (P978 §5.3's starting preset), that the default
/// grade is bit-exactly P984's, and that the two shaders it drives still declare
/// exactly the parameters the C# sets. The GPU half — the real <c>Tonemap.fx</c>
/// and <c>Bloom.fx</c> applying the grade, the HUD staying ungraded, and the
/// below-threshold highlight not blooming — runs through
/// <c>tools/p989-palette-proof</c> (the P921 proof pattern: offscreen SDL,
/// separate process) and its PASS markers are asserted below.</para>
/// </summary>
public sealed class SimPaletteTests
{
    // ── 1) the flag ───────────────────────────────────────────────────────
    [Fact]
    public void Flag_is_off_dotted_in_the_space_namespace_and_requires_the_hdr_pipeline()
    {
        Assert.Equal("space.simPalette", VisualFlags.SimPaletteKey);
        var definition = VisualFlags.Find(VisualFlags.SimPaletteKey);
        Assert.NotNull(definition);
        Assert.Equal(VisualFlagKind.Toggle, definition!.Kind);
        Assert.False(definition.DefaultOn);
        // The dependency is table data, so the options page and the use site read
        // the same fact.
        Assert.Equal(VisualFlags.HdrPipelineKey, definition.RequiresKey);
        Assert.False(new VisualOptions().SimPalette);
        // No hotkey collision on the Visuals page (R M S T G K H A U D are taken;
        // this one took the free 'P').
        Assert.Equal('P', definition.Hotkey);
        var hotkeys = VisualFlags.Definitions.Where(d => d.Hotkey != '\0').Select(d => d.Hotkey).ToList();
        Assert.Equal(hotkeys.Count, hotkeys.Distinct().Count());
    }

    [Theory]
    [InlineData("space.simPalette=on", true)]
    [InlineData("SPACE.SIMPALETTE=1", true)]
    [InlineData("space.simPalette=off", false)]
    [InlineData("space.hdrPipeline=on,space.simPalette=on", true)]
    public void Launch_override_accepts_the_dotted_key(string spec, bool expected)
    {
        var options = VisualOptions.Parse(spec, out var warnings);
        Assert.Empty(warnings);
        Assert.Equal(expected, options.SimPalette);
    }

    [Fact]
    public void Flag_persists_in_the_profile_visuals_group_and_the_override_still_wins()
    {
        string path = Path.Combine(Path.GetTempPath(), "p989-sim-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var prefs = new IntroPreferences { Visuals = new VisualOptions() };
            prefs.Visuals.SetToggle(VisualFlags.HdrPipelineKey, true);
            prefs.Visuals.SetToggle(VisualFlags.SimPaletteKey, true);
            prefs.Save(path);
            Assert.Contains("\"space.simPalette\": true", File.ReadAllText(path));

            var loaded = IntroPreferences.Load(path);
            Assert.True(loaded.Visuals!.SimPalette);
            var effective = VisualOptions.Resolve(loaded.Visuals, "space.simPalette=off", out var warnings);
            Assert.Empty(warnings);
            Assert.False(effective.SimPalette);
            // The profile is not aliased by the resolved value.
            Assert.True(loaded.Visuals!.SimPalette);
            Assert.Contains("space.simPalette=on", loaded.Visuals!.Describe());
            var fresh = VisualOptions.Resolve(null, null, out warnings);
            Assert.Empty(warnings);
            Assert.False(fresh.SimPalette);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 2) the grade: P978's starting preset, and the dependency rule ─────
    [Fact]
    public void Grade_without_the_pipeline_is_exactly_p984s_picture()
    {
        // Pipeline flag off ...
        Assert.Equal(HdrGrade.Native(HdrCurve.Neutral),
            SimPalette.Grade(requested: true, hdrActive: false, acesComparison: false));
        // ... and the flag itself off, with the pipeline on.
        Assert.Equal(HdrGrade.Native(HdrCurve.Neutral),
            SimPalette.Grade(requested: false, hdrActive: true, acesComparison: false));
        // The device refused RGBA16F: same no-op.
        Assert.Equal(HdrGrade.Native(HdrCurve.Neutral),
            SimPalette.Grade(requested: true, hdrActive: false, acesComparison: false));
        // With the pipeline on but the grade off, P984's ACES comparison still works.
        Assert.Equal(HdrCurve.Aces,
            SimPalette.Grade(requested: false, hdrActive: true, acesComparison: true).Curve);
        // Nothing else is ever graded: the default grade is the P547/P984 look.
        var native = HdrGrade.Native(HdrCurve.Neutral);
        Assert.False(native.IsSim);
        Assert.Equal(HdrOutput.DefaultExposure, native.Exposure);
        Assert.Equal(1f, native.Saturation);
        Assert.Equal(BloomPostProcessor.DefaultThreshold, native.BloomThreshold);
        Assert.Equal(BloomPostProcessor.DefaultBloomScale, native.BloomContribution);
        Assert.False(SimPalette.Applies(requested: true, hdrActive: false));
        Assert.True(SimPalette.Applies(requested: true, hdrActive: true));
    }

    [Fact]
    public void Grade_with_the_pipeline_is_the_p978_starting_preset()
    {
        var grade = SimPalette.Grade(requested: true, hdrActive: true, acesComparison: false);
        Assert.Equal(HdrCurve.Aces, grade.Curve);            // filmic (ACES-approximate)
        Assert.Equal(0.9f, grade.Exposure, 6);
        Assert.Equal(0.75f, grade.Saturation, 6);
        Assert.Equal(0.08f, grade.BloomContribution, 6);
        Assert.True(grade.IsSim);
        // No tint / lift: the grade is curve + exposure + saturation only.
        Assert.Equal(Vector3.One, SimPalette.Tint);
        // P984's own ACES comparison and the sim grade agree on the curve.
        Assert.Equal(HdrCurve.Aces,
            SimPalette.Grade(requested: true, hdrActive: true, acesComparison: true).Curve);
        Assert.Contains("simPalette ACTIVE", SimPalette.Describe(grade, requested: true, hdrActive: true));
        Assert.Contains("curve=aces", SimPalette.Describe(grade, requested: true, hdrActive: true));
        Assert.Contains("exposure=0.90", SimPalette.Describe(grade, requested: true, hdrActive: true));
        Assert.Contains("saturation=0.75", SimPalette.Describe(grade, requested: true, hdrActive: true));
        Assert.Contains("INACTIVE", SimPalette.Describe(grade, requested: true, hdrActive: false));
        Assert.Contains("off", SimPalette.Describe(HdrGrade.Native(HdrCurve.Neutral), requested: false, hdrActive: true));
    }

    [Fact]
    public void Bloom_threshold_is_stated_in_linear_light_and_read_in_the_targets_stored_domain()
    {
        // P978 states 1.2 in linear light; the float scene target stores
        // display-encoded sRGB with headroom, so the bright pass must read the
        // UNCLAMPED sRGB encode of 1.2 (~1.083), which is above white.
        Assert.Equal(1.2f, SimPalette.BloomThresholdLinear, 6);
        float stored = SimPalette.BloomThresholdStored;
        Assert.True(stored > 1f && stored < 1.1f, $"stored threshold {stored}");
        Assert.Equal(1.0833f, stored, 3);
        // HdrOutput.Encode clamps at 1 (it is the display write); ToStored must not.
        Assert.Equal(1f, HdrOutput.Encode(1.2f), 6);
        Assert.Equal(stored, HdrOutput.ToStored(1.2f), 6);
        // And the round trip lands back on 1.2 linear.
        Assert.Equal(1.2f, HdrOutput.Decode(stored), 3);
        // The grade carries the stored value, not the linear one.
        Assert.Equal(stored, SimPalette.Grade(true, true, false).BloomThreshold, 6);
    }

    // ── 3) saturation is a luma-preserving linear world grade ─────────────
    [Fact]
    public void Saturation_preserves_luma_and_pulls_chroma_toward_grey()
    {
        var colour = new Vector3(HdrOutput.Decode(0.9f), HdrOutput.Decode(0.35f), HdrOutput.Decode(0.15f));
        float Luma(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
        var calm = HdrOutput.Saturate(colour, SimPalette.Saturation);
        Assert.Equal(Luma(colour), Luma(calm), 5);
        Assert.True(calm.X < colour.X && calm.Z > colour.Z);
        // Unity is an exact no-op (that is what keeps the default grade bit-exact).
        Assert.Equal(colour, HdrOutput.Saturate(colour, 1f));
        // Grey is unaffected by any saturation.
        var grey = new Vector3(0.4f, 0.4f, 0.4f);
        Assert.Equal(grey, HdrOutput.Saturate(grey, 0.5f));
    }

    [Fact]
    public void Output_mirror_applies_exposure_and_saturation_once_in_linear_light()
    {
        foreach (float stored in new[] { 0.05f, 0.35f, 0.75f, 1.2f })
        {
            var stored3 = new Vector3(stored, stored * 0.6f, stored * 0.3f);
            var grade = SimPalette.Grade(true, true, false);
            var expected = HdrOutput.Encode(HdrOutput.Aces(
                HdrOutput.Saturate(HdrOutput.Decode(stored3) * grade.Exposure, grade.Saturation)));
            var actual = HdrOutput.Output(stored3, grade.Curve, grade.Exposure, grade.Saturation);
            Assert.Equal(expected.X, actual.X, 6);
            Assert.Equal(expected.Y, actual.Y, 6);
            Assert.Equal(expected.Z, actual.Z, 6);
            // Applying the grade twice would differ visibly for a bright sample
            // (the double-grade failure is detectable), and the filmic curve never
            // exceeds display white.
            var twice = HdrOutput.Output(actual, grade.Curve, grade.Exposure, grade.Saturation);
            if (stored > 1f) Assert.True(MathF.Abs(twice.X - actual.X) > 0.01f);
            Assert.InRange(actual.X, 0f, 1f);
        }
        // The neutral curve is untouched by P989: the same value, the same bytes.
        var sample = new Vector3(0.9f, 0.4f, 0.2f);
        Assert.Equal(HdrOutput.Neutral(sample), HdrOutput.Output(sample, HdrCurve.Neutral, 1f, 0.85f));
    }

    [Fact]
    public void The_grade_never_reaches_the_hud_and_adds_no_pass()
    {
        // Ordering: the transform is the last 3-D pass and the HUD draws after it,
        // exactly like P984 — the grade only changes transform/bloom knobs.
        var plan = HdrOutput.Plan(hdr: true, bloom: true, fsr: true);
        Assert.True(HdrOutput.RunsBefore(plan, ScenePass.OutputTransform, ScenePass.Hud));
        Assert.Equal(1, plan.Count(p => p == ScenePass.OutputTransform));
        Assert.Equal(ScenePass.Hud, plan[^1]);
        // The transform's shader samples ONE texture (the scene): there is no HUD
        // input to grade, and the pass count is unchanged from P984.
        string tonemap = File.ReadAllText(Path.Combine(ShadersDir(), "Tonemap.fx"));
        Assert.Equal(1, Occurrences(tonemap, "texture SceneTexture;"));
        Assert.DoesNotContain("HudTexture", tonemap);
        Assert.Equal(1, Occurrences(tonemap, "technique Output"));
    }

    // ── 4) the shaders are driven by code, and stay small ─────────────────
    [Fact]
    public void Tonemap_shader_applies_the_world_saturation_only_in_the_filmic_pass()
    {
        string source = File.ReadAllText(Path.Combine(ShadersDir(), "Tonemap.fx"));
        Assert.Contains("float Saturation", source);
        string neutral = SourceBetween(source, "float4 PS_Neutral(", "// Aces:");
        Assert.DoesNotContain("Saturation", neutral);          // P984's curve untouched
        string aces = SourceBetween(source, "float4 PS_Aces(", "technique Output");
        Assert.Contains("ApplySaturation(lin, Saturation)", aces);
        Assert.Contains("SrgbDecode(max(tex2D(SceneSampler, i.UV).rgb, 0.0)) * Exposure", aces);
        // Every uniform the C# sets is declared (GL ignores shader defaults).
        foreach (string parameter in HdrOutputTransform.RequiredParameters)
            Assert.Contains(parameter, source);
        Assert.Contains("Saturation", HdrOutputTransform.RequiredParameters);
        Assert.DoesNotContain("ToneMap(", neutral);             // one curve, not two
    }

    [Fact]
    public void Bloom_shader_takes_the_sim_contribution_and_the_default_is_the_p547_look()
    {
        // No shader change: the palette's glow contribution IS the bright-pass
        // scale Bloom.fx already has (so the default path's bytecode is P547's).
        string source = File.ReadAllText(Path.Combine(ShadersDir(), "Bloom.fx"));
        Assert.DoesNotContain("BloomContribution", source);
        Assert.Contains("float BloomScale", source);
        Assert.Contains("BloomScale", BloomPostProcessor.RequiredParameters);
        Assert.DoesNotContain("BloomContribution", BloomPostProcessor.RequiredParameters);
        // The defaults reproduce P547 exactly (no new pass, no new target): the
        // resolved grade carries P547's threshold and a unity contribution.
        Assert.Equal(0.80f, BloomPostProcessor.DefaultThreshold, 6);
        Assert.Equal(1f, BloomPostProcessor.DefaultBloomScale);
        var native = HdrGrade.Native(HdrCurve.Neutral);
        Assert.Equal(BloomPostProcessor.DefaultThreshold, native.BloomThreshold);
        Assert.Equal(BloomPostProcessor.DefaultBloomScale, native.BloomContribution);
    }

    [Fact]
    public void The_4k_fold_exists_but_the_shipping_frame_still_runs_the_separate_transform()
    {
        // The fold (RCAS + the output transform in one pass, so the 4K path needs no
        // extra RGBA16F target) exists and is measured by tools/p989-palette-proof:
        // the folded output differs from the unfolded one by ONE 8-bit step on ~2.7 %
        // of pixels (the RGBA16F round trip), so the "pixel-equivalent only" rule
        // keeps the separate transform. This pins the decision: the passes stay (a
        // one-line change adopts them if 1 LSB is ever acceptable) and the frame
        // path keeps exactly one, un-folded output transform.
        string rcas = File.ReadAllText(Path.Combine(ShadersDir(), "FsrRcas.fx"));
        Assert.Contains("pass RcasNeutralGraded", rcas);
        Assert.Contains("pass RcasAcesGraded", rcas);
        Assert.Contains("float Exposure", rcas);
        string render = File.ReadAllText(Path.Combine(RepoRoot(), "src/Allegiance.ClientCore.Game/Game1.Rendering.cs"));
        Assert.Contains("if (outputTransform) _post!.Output!.Render(upscaleSource!, finalTarget, _grade);", render);
        Assert.DoesNotContain("foldGrade", render);
        // FSR still writes its float upscale target and the transform reads it.
        Assert.Contains("_fsr!.Render(upscaleSource!, hdr ? null : finalTarget, _upscaling.Sharpness, hdr);", render);
    }

    // ── 5) the options row says it needs the HDR scene ────────────────────
    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Visuals_row_explains_the_dependency_in_both_hud_styles(HudStyle style)
    {
        Assert.Equal(style, new HudSettings { Style = style }.Style);
        var visuals = new VisualOptions();
        visuals.SetToggle(VisualFlags.SimPaletteKey, true);
        // The device decided: no float scene path -> the row says the flag is inactive.
        var inactive = State(visuals, hdrSceneActive: false);
        var rows = VisualsRows(inactive);
        Assert.Contains(rows, r => r.Label.StartsWith("Sim palette: on - ")
            && r.Label.EndsWith(" (inactive: needs HDR scene)"));
        // The pipeline really is active -> no note.
        var active = State(visuals, hdrSceneActive: true);
        var activeRows = VisualsRows(active);
        Assert.Contains(activeRows, r => r.Label.StartsWith("Sim palette: on - ")
            && !r.Label.Contains("inactive"));
        // Before a flight session the state is unknown: the description still names
        // the requirement, but nothing claims to be inactive.
        var unknown = State(visuals, hdrSceneActive: null);
        Assert.Contains(VisualsRows(unknown), r => r.Label.StartsWith("Sim palette: on - ")
            && !r.Label.Contains("inactive"));
        // The row edits the right key and its hotkey resolves to it.
        var menu = OpenVisualsPage(inactive);
        int row = menu.Rows(inactive).ToList().FindIndex(r => r.Label.StartsWith("Sim palette: "));
        Assert.True(row >= 0);
        Assert.Equal(VisualFlags.SimPaletteKey, menu.VisualKeyAt(row, inactive));
        menu.Row0 = 0;
        Assert.Equal(OptionsMenuModel.OptionsCommand.ToggleVisualFlag, menu.CommandForHotkey('P', inactive));
        Assert.Equal(VisualFlags.SimPaletteKey, menu.VisualKeyAt(menu.Row0, inactive));
    }

    // ── 6) the GPU proof (real shaders + real targets) ────────────────────
    [SkippableFact, Trait("Requires", "Gpu"), Trait("Tier", "Live")]
    public async Task Gpu_proof_applies_the_sim_grade_bloom_and_keeps_the_hud_ungraded()
    {
        string repo = RepoRoot();
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
        string dll = Path.Combine(repo, "tools/p989-palette-proof/bin/Debug/net10.0/p989-palette-proof.dll");
        Skip.IfNot(File.Exists(dll), "build tools/build-tools.sh first (p989-palette-proof)");
        var info = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(dll);
        info.ArgumentList.Add("--check");
        info.ArgumentList.Add(Path.Combine(repo, "src/Allegiance.ClientCore.MonoGame/Content/Shaders"));
        info.Environment["SDL_VIDEODRIVER"] = "offscreen";
        info.Environment["ALSOFT_DRIVERS"] = "null";
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); Assert.Fail("P989 palette proof timed out"); }
        string log = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, log);
        Assert.Contains("P989_PALETTE_TRANSFORM_PASS", log);
        Assert.Contains("P989_PALETTE_FLAGOFF_PASS", log);
        Assert.Contains("P989_PALETTE_HUD_PASS", log);
        Assert.Contains("P989_PALETTE_BLOOM_PASS", log);
        // The 4K fold is measured on the same run; it reports its delta either way.
        Assert.Contains("P989_PALETTE_FOLD_", log);
        Assert.Contains("P989_PALETTE_CHECK_PASS", log);
    }

    // ── helpers ───────────────────────────────────────────────────────────
    private static OptionsMenuModel.OptionsState State(VisualOptions visuals, bool? hdrSceneActive)
        => new(MusicOn: true, FilterChatsToAll: true, LinearControls: false, FlipY: false,
            VirtualJoystick: true, Deadzone: 0.1f, Visuals: visuals, HdrSceneActive: hdrSceneActive);

    private static OptionsMenuModel OpenVisualsPage(OptionsMenuModel.OptionsState state)
    {
        var menu = new OptionsMenuModel();
        int row = menu.Rows(state).ToList().FindIndex(r => r.Command == OptionsMenuModel.OptionsCommand.OpenVisualsMenu);
        Assert.True(row >= 0);
        menu.Row0 = row;
        Assert.Null(menu.Activate(state));
        Assert.Equal(OptionsMenuModel.Page.Visuals, menu.Current);
        return menu;
    }

    private static System.Collections.Generic.IReadOnlyList<OptionsMenuModel.Row> VisualsRows(
        OptionsMenuModel.OptionsState state) => OpenVisualsPage(state).Rows(state);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ShadersDir() => Path.Combine(RepoRoot(), "src/Allegiance.ClientCore.Game/Shaders");

    private static int Occurrences(string text, string needle)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    private static string SourceBetween(string text, string from, string to)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing '{from}'");
        int end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"missing '{to}' after '{from}'");
        return text[start..end];
    }
}
