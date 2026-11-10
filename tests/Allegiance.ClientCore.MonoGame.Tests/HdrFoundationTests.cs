using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P984 — the linear HDR scene foundation (<c>visuals.space.hdrPipeline</c>,
/// default OFF): the float scene target, the ONE output transform
/// (<see cref="HdrOutput"/>, <c>Shaders/Tonemap.fx</c>), the ordered pass list
/// (bloom before the transform, HUD after it), the render-target format and its
/// fallback, and the flag's parsing/persistence through the P979 framework.
/// <para>The pure tests here mirror the HLSL; the GPU tests run the real
/// <c>Tonemap.ogl.mgfxo</c> and the real <see cref="VfxPostProcessor"/> through
/// <c>tools/p984-hdr-proof</c> (the P921 pattern: offscreen SDL, separate
/// process) and assert its PASS markers, so a shader/host mismatch fails the
/// suite instead of only the report.</para>
/// </summary>
public sealed class HdrFoundationTests
{
    // ── 1) the ordered pass list ──────────────────────────────────────────
    [Fact]
    public void Flag_off_plan_is_todays_pipeline_with_no_output_transform()
    {
        var plain = HdrOutput.Plan(hdr: false, bloom: false, fsr: false);
        Assert.Equal(new[] { ScenePass.Scene, ScenePass.Hud }, plain);
        var bloomed = HdrOutput.Plan(hdr: false, bloom: true, fsr: true);
        Assert.Equal(new[]
        {
            ScenePass.Scene, ScenePass.BloomBright, ScenePass.BloomBlur, ScenePass.BloomComposite,
            ScenePass.FsrUpscale, ScenePass.Hud,
        }, bloomed);
        Assert.Equal(-1, HdrOutput.IndexOf(bloomed, ScenePass.OutputTransform));
    }

    [Fact]
    public void Hdr_plan_runs_bloom_then_fsr_then_the_one_transform_then_the_hud()
    {
        var plan = HdrOutput.Plan(hdr: true, bloom: true, fsr: true);
        Assert.Equal(new[]
        {
            ScenePass.Scene, ScenePass.BloomBright, ScenePass.BloomBlur, ScenePass.BloomComposite,
            ScenePass.FsrUpscale, ScenePass.OutputTransform, ScenePass.Hud,
        }, plan);
        // The brief's order rules, spelled out.
        Assert.True(HdrOutput.RunsBefore(plan, ScenePass.BloomComposite, ScenePass.OutputTransform));
        Assert.True(HdrOutput.RunsBefore(plan, ScenePass.FsrUpscale, ScenePass.OutputTransform));
        Assert.True(HdrOutput.RunsBefore(plan, ScenePass.OutputTransform, ScenePass.Hud));
        Assert.False(HdrOutput.RunsBefore(plan, ScenePass.OutputTransform, ScenePass.OutputTransform));
        // Exactly one output transform, whatever the bloom/FSR combination.
        foreach (bool bloom in new[] { false, true })
            foreach (bool fsr in new[] { false, true })
                Assert.Equal(1, HdrOutput.Plan(true, bloom, fsr).Count(p => p == ScenePass.OutputTransform));
        Assert.Equal("hdr=on curve=neutral passes=Scene>BloomBright>BloomBlur>BloomComposite>FsrUpscale>OutputTransform>Hud",
            HdrOutput.Describe(hdr: true, HdrCurve.Neutral, bloom: true, fsr: true));
        Assert.Contains("curve=aces", HdrOutput.Describe(hdr: true, HdrCurve.Aces, bloom: false, fsr: false));
    }

    // ── 2) render-target format + fallback decision ───────────────────────
    [Fact]
    public void Scene_format_is_float_only_on_the_hdr_path()
    {
        Assert.Equal(SurfaceFormat.HalfVector4, HdrOutput.SceneFormat(true));
        Assert.Equal(SurfaceFormat.Color, HdrOutput.SceneFormat(false));
    }

    // ── 3) the neutral curve reproduces today's picture ───────────────────
    [Fact]
    public void Neutral_curve_is_the_identity_below_the_shoulder_onset()
    {
        foreach (float v in new[] { 0f, 0.05f, 0.25f, 0.5f, 0.79f })
        {
            var c = new Vector3(v, v * 0.8f, v * 0.6f);
            var mapped = HdrOutput.Neutral(c);
            Assert.Equal(c.X, mapped.X, 6);
            Assert.Equal(c.Y, mapped.Y, 6);
            Assert.Equal(c.Z, mapped.Z, 6);
        }
    }

    [Fact]
    public void Neutral_is_exactly_the_hd6_pbr_curve_that_the_shader_used_to_run()
    {
        // P984 replaced NativePbr.fx's in-shader ToneMap with this one central
        // curve, so for the default tuning the two must agree (that is what
        // keeps the neutral HDR picture where the native 8-bit one was).
        var tuning = MaterialTuning.Default;
        foreach (var c in new[]
                 {
                     new Vector3(0.1f, 0.2f, 0.3f), new Vector3(0.85f, 0.8f, 0.75f),
                     new Vector3(1.2f, 0.3f, 0.2f), new Vector3(2.5f, 2.4f, 2.2f),
                 })
        {
            var expected = MaterialTuning.ToneMap(c, tuning);
            expected = new Vector3(Math.Clamp(expected.X, 0f, 1f), Math.Clamp(expected.Y, 0f, 1f),
                Math.Clamp(expected.Z, 0f, 1f));
            var actual = HdrOutput.Neutral(c);
            Assert.Equal(expected.X, actual.X, 6);
            Assert.Equal(expected.Y, actual.Y, 6);
            Assert.Equal(expected.Z, actual.Z, 6);
        }
        // Hue-preserving: the peak channel is compressed, the ratio is kept, and
        // a bright sample never exceeds the display range.
        var bright = HdrOutput.Neutral(new Vector3(1.6f, 0.8f, 0.4f));
        Assert.True(bright.X <= 1f && bright.X >= 0.8f);
        Assert.True(bright.Y < bright.X && bright.Z < bright.Y);
    }

    // ── 4) ACES: one transform, in linear space ───────────────────────────
    [Fact]
    public void Aces_decodes_applies_the_fit_and_encodes_once()
    {
        foreach (float stored in new[] { 0.02f, 0.25f, 0.5f, 0.9f, 1.5f })
        {
            float linear = HdrOutput.Decode(stored);
            float expected = HdrOutput.Encode(HdrOutput.Aces(linear));
            Assert.Equal(expected, HdrOutput.Output(new Vector3(stored), HdrCurve.Aces).X, 6);
        }
        // sRGB transfer round trip.
        foreach (float v in new[] { 0f, 0.01f, 0.25f, 0.5f, 0.75f, 1f })
            Assert.Equal(v, HdrOutput.Encode(HdrOutput.Decode(v)), 5);
        // The filmic fit is monotone and bounded.
        float previous = -1f;
        foreach (float linear in new[] { 0f, 0.05f, 0.18f, 0.5f, 1f, 4f, 16f, 100f })
        {
            float mapped = HdrOutput.Aces(linear);
            Assert.True(mapped >= previous);
            Assert.InRange(mapped, 0f, 1f);
            previous = mapped;
        }
        Assert.Equal(0f, HdrOutput.Aces(-1f));
    }

    [Fact]
    public void A_known_linear_input_maps_to_the_expected_output_exactly_once()
    {
        // The "no double tone mapping" proof in numbers: 18% linear grey is
        // stored as sRGB 0.4614 (118/255); one ACES application gives a value the
        // tests pin. Applying the curve a second time (the failure the brief
        // calls out — PBR's own gamma curve runs on top of the transform's) lands
        // somewhere else entirely, and the two must not be confused.
        float stored = HdrOutput.Encode(0.18f);
        Assert.Equal(0.4614, (double)stored, 4);
        float once = HdrOutput.Output(new Vector3(stored), HdrCurve.Aces).X;
        Assert.Equal(HdrOutput.Encode(HdrOutput.Aces(0.18f)), once, 6);
        Assert.Equal(0.5535, (double)once, 4);          // ACES(0.18) ≈ 0.2613 → sRGB encode ≈ 0.5535
        float twice = HdrOutput.Encode(HdrOutput.Aces(HdrOutput.Decode(once)));
        Assert.True(Math.Abs(twice - once) > 0.1f,
            $"a second application must visibly differ ({once} vs {twice})");
        // Neutral keeps the same sample at its stored display value.
        Assert.Equal(stored, HdrOutput.Output(new Vector3(stored), HdrCurve.Neutral).X, 6);
    }

    [Fact]
    public void Exposure_is_unity_in_this_slice_and_scales_in_the_curves_domain()
    {
        Assert.Equal(1f, HdrOutput.DefaultExposure);
        // Neutral applies exposure before the shoulder (the PBR shader's old order).
        var neutralUnity = HdrOutput.Neutral(new Vector3(0.5f));
        var neutralDoubled = HdrOutput.Neutral(new Vector3(0.25f), 2f);
        Assert.Equal((double)neutralUnity.X, (double)neutralDoubled.X, 6);
        Assert.Equal((double)neutralUnity.Y, (double)neutralDoubled.Y, 6);
        // ACES applies it in linear space.
        Assert.Equal((double)HdrOutput.Encode(HdrOutput.Aces(0.18f)),
            (double)HdrOutput.Output(new Vector3(HdrOutput.Encode(0.09f)), HdrCurve.Aces, 2f).X, 5);
    }

    // ── 5) the two passes are one technique, and the PBR path has no curve ─
    [Fact]
    public void Tonemap_shader_declares_exactly_one_technique_with_neutral_and_aces_passes()
    {
        string source = File.ReadAllText(Path.Combine(ShaderDir(), "Tonemap.fx"));
        Assert.Contains("technique Output", source);
        Assert.Single(Techniques(source));
        Assert.Contains("pass Neutral", source);
        Assert.Contains("pass Aces", source);
        // One texture input and the three uniforms the transform sets from code.
        Assert.Contains("texture SceneTexture;", source);
        foreach (string parameter in HdrOutputTransform.RequiredParameters)
            Assert.Contains(parameter, source);
        // The shader must not apply its own curve twice: exactly one shoulder and
        // one filmic fit, each reached from one pass.
        Assert.Equal(1, Occurrences(source, "float3 Aces("));
        Assert.Equal(1, Occurrences(source, "float4 PS_Neutral("));
        Assert.Equal(1, Occurrences(source, "float4 PS_Aces("));
    }

    [Fact]
    public void Pbr_hdr_techniques_have_no_tone_curve_and_the_native_ones_keep_it()
    {
        string source = File.ReadAllText(Path.Combine(ShaderDir(), "NativePbr.fx"));
        Assert.Contains("technique NativePbrHdr", source);
        Assert.Contains("technique EffectLitPbrHdr", source);
        // Native (flag OFF) techniques still run the HD-6 gamma curve — pinned so
        // the default path cannot drift.
        Assert.Contains("technique NativePbr { pass Pass0 { VertexShader=compile VS_SHADERMODEL VS(); PixelShader=compile PS_SHADERMODEL PS(); } }", source);
        Assert.Contains("technique EffectLitPbr { pass Pass0 { VertexShader=compile VS_SHADERMODEL VS(); PixelShader=compile PS_SHADERMODEL PSFlash(); } }", source);
        string hdr = SourceBetween(source, "float4 PSHdr(VOUT i):COLOR0", "technique NativePbrHdr");
        Assert.DoesNotContain("ToneMap(", hdr);
        Assert.DoesNotContain("clamp(ToneMap", hdr);
        Assert.Contains("LinearToSrgb3", hdr);       // the write transfer, once
        Assert.Contains("SrgbToLinear3", hdr);       // linear lighting
    }

    // ── 6) the flag: parsing, persistence, options page ───────────────────
    [Fact]
    public void Hdr_flags_default_off_and_use_the_space_namespace()
    {
        var options = new VisualOptions();
        Assert.True(options.HdrPipeline);   // default ON since 2026-11-09
        Assert.False(options.HdrAces);
        Assert.Equal("space.hdrPipeline", VisualFlags.HdrPipelineKey);
        Assert.Equal("space.hdrAces", VisualFlags.HdrAcesKey);
        Assert.Equal(VisualFlagKind.Toggle, VisualFlags.Find(VisualFlags.HdrPipelineKey)!.Kind);
        Assert.True(VisualFlags.Find(VisualFlags.HdrPipelineKey)!.DefaultOn);
        Assert.False(VisualFlags.Find(VisualFlags.HdrAcesKey)!.DefaultOn);
        // No hotkey collision on the Visuals page (each row owns one letter).
        var hotkeys = VisualFlags.Definitions.Where(d => d.Hotkey != '\0').Select(d => d.Hotkey).ToList();
        Assert.Equal(hotkeys.Count, hotkeys.Distinct().Count());
    }

    [Theory]
    [InlineData("space.hdrPipeline=on", true, false)]
    [InlineData("space.hdrPipeline=on,space.hdrAces=on", true, true)]
    [InlineData("SPACE.HDRPIPELINE=1,space.hdrAces=true", true, true)]
    [InlineData("space.hdrPipeline=off,space.hdrAces=on", false, true)]
    public void Launch_override_accepts_the_dotted_keys(string spec, bool pipeline, bool aces)
    {
        var options = VisualOptions.Parse(spec, out var warnings);
        Assert.Empty(warnings);
        Assert.Equal(pipeline, options.HdrPipeline);
        Assert.Equal(aces, options.HdrAces);
    }

    [Fact]
    public void Hdr_flags_persist_in_the_profile_visuals_group()
    {
        string path = Path.Combine(Path.GetTempPath(), "p984-hdr-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var prefs = new IntroPreferences { Visuals = new VisualOptions() };
            prefs.Visuals.SetToggle(VisualFlags.HdrPipelineKey, true);
            prefs.Visuals.SetToggle(VisualFlags.HdrAcesKey, true);
            prefs.Save(path);
            string written = File.ReadAllText(path);
            Assert.Contains("\"space.hdrPipeline\": true", written);
            Assert.Contains("\"space.hdrAces\": true", written);

            var loaded = IntroPreferences.Load(path);
            Assert.True(loaded.Visuals!.HdrPipeline);
            Assert.True(loaded.Visuals!.HdrAces);
            // The launch override still wins, and the profile is not aliased.
            var effective = VisualOptions.Resolve(loaded.Visuals, "space.hdrPipeline=off", out var warnings);
            Assert.Empty(warnings);
            Assert.False(effective.HdrPipeline);
            Assert.True(loaded.Visuals!.HdrPipeline);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void A_profile_without_the_group_gets_hdr_on_and_neutral()
    {
        var resolved = VisualOptions.Resolve(null, null, out var warnings);
        Assert.Empty(warnings);
        Assert.True(resolved.HdrPipeline);   // default ON since 2026-11-09
        Assert.False(resolved.HdrAces);
    }

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Visuals_page_lists_the_hdr_rows_in_both_hud_styles(HudStyle style)
    {
        // The page is shared model code; HudStyle only selects the in-flight
        // renderer, so both styles must show (and toggle) the same rows.
        var settings = new HudSettings { Style = style };
        Assert.Equal(style, settings.Style);
        var state = new OptionsMenuModel.OptionsState(
            MusicOn: true, FilterChatsToAll: true, LinearControls: false,
            FlipY: false, VirtualJoystick: true, Deadzone: 0.1f, Visuals: new VisualOptions());
        var menu = new OptionsMenuModel();
        menu.Row0 = menu.Rows(state).ToList().FindIndex(r => r.Command == OptionsMenuModel.OptionsCommand.OpenVisualsMenu);
        Assert.Null(menu.Activate(state));
        Assert.Equal(OptionsMenuModel.Page.Visuals, menu.Current);
        var rows = menu.Rows(state);
        Assert.Contains(rows, r => r.Label.StartsWith("HDR scene: on - "));
        Assert.Contains(rows, r => r.Label.StartsWith("ACES output curve: off - "));
        int hdrRow = rows.ToList().FindIndex(r => r.Label.StartsWith("HDR scene: "));
        int acesRow = rows.ToList().FindIndex(r => r.Label.StartsWith("ACES output curve: "));
        Assert.Equal(VisualFlags.HdrPipelineKey, menu.VisualKeyAt(hdrRow, state));
        Assert.Equal(VisualFlags.HdrAcesKey, menu.VisualKeyAt(acesRow, state));
        Assert.Equal(OptionsMenuModel.OptionsCommand.ToggleVisualFlag, rows[hdrRow].Command);
    }

    // ── 7) the GPU proof (real shader + real render targets) ──────────────
    [SkippableFact, Trait("Requires", "Gpu"), Trait("Tier", "Live")]
    public async Task Gpu_proof_shows_one_transform_float_targets_resize_and_fallback()
    {
        string repo = RepoRoot();
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
        string dll = Path.Combine(repo, "tools/p984-hdr-proof/bin/Debug/net10.0/p984-hdr-proof.dll");
        Skip.IfNot(File.Exists(dll), "build tools/build-tools.sh first (p984-hdr-proof)");
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
        catch (OperationCanceledException) { process.Kill(true); Assert.Fail("P984 HDR proof timed out"); }
        string log = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, log);
        Assert.Contains("P984_HDR_TRANSFORM_PASS", log);
        Assert.Contains("P984_HDR_ORDER_PASS", log);
        Assert.Contains("P984_HDR_TARGET_PASS", log);
        Assert.Contains("P984_HDR_CHECK_PASS", log);
    }

    // ── helpers ───────────────────────────────────────────────────────────
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ShaderDir() => Path.Combine(RepoRoot(), "src/Allegiance.ClientCore.Game/Shaders");

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

    private static string[] Techniques(string source)
    {
        var names = new System.Collections.Generic.List<string>();
        int at = 0;
        while ((at = source.IndexOf("technique ", at, StringComparison.Ordinal)) >= 0)
        {
            int start = at + "technique ".Length;
            int end = start;
            while (end < source.Length && (char.IsLetterOrDigit(source[end]) || source[end] == '_')) end++;
            names.Add(source[start..end]);
            at = end;
        }
        return names.ToArray();
    }
}
