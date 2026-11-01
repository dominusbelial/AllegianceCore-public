using System.IO;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.StaticCoreText;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>P908 review — the resolver every real game client installs falls
/// back to a text core through its asset source (missionigc.cpp:2637-2672).</summary>
public class TextCoreAssetsTests
{
    private static string ExportedCore()
    {
        Require.Artwork();
        string root = Path.Combine(Path.GetTempPath(), "p908-assets-" + Guid.NewGuid().ToString("N"));
        StaticCoreRoundtrip.Export(IgcReader.ParseFile(Path.Combine(ExternalPaths.Require(ExternalResourceKind.Artwork), "PCore001.igc")),
            Path.Combine(root, "text"));
        return root;
    }

    [SkippableFact]
    public void Text_folder_loads_through_asset_source_as_version_1337()
    {
        string root = ExportedCore();
        try
        {
            var core = TextCoreAssets.TryLoad(new DirectoryAssetSource(root), "text");
            var binary = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ExternalPaths.Require(ExternalResourceKind.Artwork), "PCore001.igc")));
            Assert.NotNull(core);
            Assert.Equal(StaticCoreCache.TextVersion, core.Version);
            Assert.Equal(binary.HullTypes.Count, core.HullTypes.Count);
            Assert.Equal(binary.Civilizations.Count, core.Civilizations.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [SkippableFact]
    public void Absent_folder_is_null_and_malformed_text_is_refused()
    {
        string root = ExportedCore();
        try
        {
            var assets = new DirectoryAssetSource(root);
            Assert.Null(TextCoreAssets.TryLoad(assets, "missing"));
            string constants = Path.Combine(root, "text", "Constants.csv");
            File.WriteAllLines(constants, File.ReadAllLines(constants)[..1]);
            Assert.Throws<InvalidDataException>(() => TextCoreAssets.TryLoad(assets, "text"));
        }
        finally { Directory.Delete(root, true); }
    }
}
