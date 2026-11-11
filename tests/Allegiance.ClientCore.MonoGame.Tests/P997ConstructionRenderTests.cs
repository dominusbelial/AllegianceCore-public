using System.Diagnostics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.ClientCore.State;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P997ConstructionRenderTests
{
    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Fallback_notice_names_type_and_model_once_for_either_HUD(HudStyle style)
    {
        Assert.True(Enum.IsDefined(style));
        var warnings = new StationFallbackWarnings(); var world = new ClientWorld();
        var line = warnings.Take(12, "Outpost", "ss92");
        Assert.NotNull(line); Assert.Contains("Outpost", line); Assert.Contains("ss92", line);
        world.PostSystemLine(line);
        Assert.Null(warnings.Take(12, "Outpost", "SS92"));
        Assert.NotNull(warnings.Take(13, "Garrison", "ss92"));
        Assert.Contains("<empty>", warnings.Take(14, "Unknown", null)!);
        Assert.Single(world.Events.Chats);
    }

    [Fact]
    public void Shell_colour_is_native_violet_and_its_HSB_complement()
    {
        Assert.Equal(new Vector3(50f / 255, 28f / 255, 146f / 255), BuildingEffectRenderer.OuterColor);
        Assert.Equal(new Vector3(124f / 255, 146f / 255, 28f / 255), BuildingEffectRenderer.InnerColor);
        Assert.Equal(1.1f, BuildingEffectRenderer.ShellRatio);
    }

    [Fact]
    public void Shrink_fades_outer_RGB_once_while_keeping_its_native_alpha()
    {
        var phase = Allegiance.Core.Sim.ConstructionPhase.At(35, default, default, 50, 100);
        var colors = BuildingEffectRenderer.ColorsFor(phase);
        Assert.Equal(.5f, colors.InnerAlpha);
        Assert.Equal(1, colors.OuterAlpha); // Color * intensity leaves alpha alone (zlib/color.h:101-110).
        Assert.Equal(new Vector3(25f / 255, 14f / 255, 73f / 255), colors.OuterRgb);
        Assert.Equal(new Vector3(62f / 255, 73f / 255, 14f / 255), colors.InnerRgb);
    }

    [Fact]
    public void Shell_rotations_use_the_native_independent_axes_and_rates()
    {
        // Native reference samples at window time 1: Y(pi/2), Z(1/pi)
        // for the inner shell; Y(pi), X(1) for the outer shell.
        var inner = Vector3.Transform(Vector3.UnitZ, BuildingEffectRenderer.InnerRotation(1));
        var outer = Vector3.Transform(Vector3.UnitZ, BuildingEffectRenderer.OuterRotation(1));
        Assert.True(Vector3.Distance(new(.9497657f, .3129618f, 0), inner) < .00001f);
        Assert.True(Vector3.Distance(new(0, .841471f, -.5403023f), outer) < .00001f);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Every_PCore001_station_model_decodes_in_artwork_CI_without_a_graphics_device()
    {
        Require.Artwork();
        string art = ExternalPaths.Require(ExternalResourceKind.Artwork);
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(art, "PCore001.igc")));
        var assets = new DirectoryAssetSource(art);
        Assert.NotEmpty(core.StationTypes);
        foreach (var type in core.StationTypes.Values)
        {
            string stem = type.ModelName.Trim();
            Assert.True(assets.Exists(stem + ".mdl"), $"Missing station {type.StationTypeId} '{type.Name}': {stem}");
            // This is the actual DecodeModel entry point called by MeshFor,
            // including its namespace/import and authored mesh decoding.
            var file = NativeStationRenderer.DecodeModel(assets, stem);
            Assert.Contains(file.Meshes, mesh => mesh.VertexCount > 0 && mesh.Indices.Length >= 3);
            Assert.True(NativeStationResolver.MeshRadius(file.Meshes, file.LodLevels) > 0,
                $"Station {type.StationTypeId} '{type.Name}': {stem} has no renderable extent");
        }
    }

    [SkippableFact, Trait("Requires", "Artwork"), Trait("Requires", "Gpu"), Trait("Tier", "Live")]
    public async Task Every_PCore001_station_type_resolves_and_decodes_through_NativeStationRenderer()
    {
        Require.Artwork();
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AllegianceCore.slnx"))) repo = repo.Parent;
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var info = new ProcessStartInfo(host) { WorkingDirectory = repo!.FullName,
            RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(repo.FullName, "tools/p997-construction-proof/bin", configuration,
            "net10.0/p997-construction-proof.dll"));
        info.ArgumentList.Add("--models");
        info.Environment["SDL_VIDEODRIVER"] = "offscreen"; info.Environment["ALSOFT_DRIVERS"] = "null";
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(true); await process.WaitForExitAsync();
            Assert.Fail("P997 station-model audit timed out");
        }
        string log = await stdout + await stderr;
        Require.GraphicsDevice(log);
        Assert.True(process.ExitCode == 0, log);
        Assert.Contains("P997_ALL_STATIONS_PASS", log);
        Assert.Contains("models=81", log);
    }
}
