using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P762 — native sector-map threat/selection artwork mapping
/// (WinTrek/sectormap.cpp:1056-1099). Native blits sectorwarning/capital/
/// bomber/miner/combat BMPs by the warning cascade, then sectorhighlight for
/// the chat cluster and sectorsel for the selected cluster. This pins the
/// mapping to the exact native stems and proves each stem exists in the
/// artwork tree (the same .mdl pipeline the HUD uses).
/// </summary>
public class P762StarmapArtworkTests
{
    [Fact]
    public void Threat_tiers_map_to_the_native_artwork_stems()
    {
        Assert.Equal("sectorwarningbmp", StarmapThreatPresentation.AssetStem(StarmapThreatPresentation.Tier.Station));
        Assert.Equal("sectorcapitalbmp", StarmapThreatPresentation.AssetStem(StarmapThreatPresentation.Tier.Capital));
        Assert.Equal("sectorbomberbmp", StarmapThreatPresentation.AssetStem(StarmapThreatPresentation.Tier.Bomber));
        Assert.Equal("sectorminerbmp", StarmapThreatPresentation.AssetStem(StarmapThreatPresentation.Tier.Miner));
        Assert.Equal("sectorcombatbmp", StarmapThreatPresentation.AssetStem(StarmapThreatPresentation.Tier.Combat));
        Assert.Null(StarmapThreatPresentation.AssetStem(StarmapThreatPresentation.Tier.None));

        // Native threshold cascade (sectormap.cpp:1066-1082).
        Assert.Equal(StarmapThreatPresentation.Tier.Station,
            StarmapThreatPresentation.IconTier(ClusterWarnings.StationThreatened));
        Assert.Equal(StarmapThreatPresentation.Tier.Capital,
            StarmapThreatPresentation.IconTier(ClusterWarnings.CapitalInCluster));
        Assert.Equal(StarmapThreatPresentation.Tier.Bomber,
            StarmapThreatPresentation.IconTier(ClusterWarnings.BomberInCluster));
        Assert.Equal(StarmapThreatPresentation.Tier.Miner,
            StarmapThreatPresentation.IconTier(ClusterWarnings.MinerThreatened));
        Assert.Equal(StarmapThreatPresentation.Tier.Combat,
            StarmapThreatPresentation.IconTier(ClusterWarnings.CombatInCluster));
        Assert.Equal(StarmapThreatPresentation.Tier.None,
            StarmapThreatPresentation.IconTier(ClusterWarnings.EnemyBuilderInCluster));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Every_native_sector_map_artwork_file_is_available()
    {
        Require.Artwork();
        string root = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        foreach (var stem in new[]
        {
            "sectorwarningbmp", "sectorcapitalbmp", "sectorbomberbmp",
            "sectorminerbmp", "sectorcombatbmp", "sectorhighlightbmp", "sectorselbmp",
        })
            Assert.True(File.Exists(Path.Combine(root, stem + ".mdl")), $"missing native artwork {stem}.mdl");
    }
}

/// <summary>
/// P762b — the two distinct native sector-map overlays that P762 conflated:
/// (1) <c>sectorhighlightbmp</c> belongs to the CHAT cluster
/// (<c>trekClient.GetChatCluster() == pCluster</c>, sectormap.cpp:1095), not to
/// the commander's HIGHLIGHT_CLUSTER set; (2) HIGHLIGHT_CLUSTER drives the
/// flashing cyan <c>sectorEmpty</c> outline on the off-flash phase
/// (<c>pCluster-&gt;GetHighlight() &amp;&amp; !m_bFlashFrame</c>, sectormap.cpp:1043-1046).
/// </summary>
public class P762bStarmapOverlayTests
{
    [Fact]
    public void Chat_cluster_gets_the_highlight_bitmap_and_selection_is_the_fallback()
    {
        // Chat cluster wins even when the same cluster is also selected
        // (native's if / else-if chain, sectormap.cpp:1094-1099).
        Assert.Equal(StarmapThreatPresentation.SectorOverlay.ChatCluster,
            StarmapThreatPresentation.OverlayFor(4, chatClusterId: 4, selectedClusterId: 4));
        Assert.Equal("sectorhighlightbmp",
            StarmapThreatPresentation.OverlayAssetStem(
                StarmapThreatPresentation.OverlayFor(4, chatClusterId: 4, selectedClusterId: 2)));

        // A selected non-chat cluster gets sectorselbmp.
        Assert.Equal(StarmapThreatPresentation.SectorOverlay.Selected,
            StarmapThreatPresentation.OverlayFor(2, chatClusterId: 4, selectedClusterId: 2));
        Assert.Equal("sectorselbmp",
            StarmapThreatPresentation.OverlayAssetStem(
                StarmapThreatPresentation.OverlayFor(2, chatClusterId: 4, selectedClusterId: 2)));

        // Everything else gets no bitmap.
        Assert.Equal(StarmapThreatPresentation.SectorOverlay.None,
            StarmapThreatPresentation.OverlayFor(3, chatClusterId: 4, selectedClusterId: 2));
        Assert.Null(StarmapThreatPresentation.OverlayAssetStem(
            StarmapThreatPresentation.OverlayFor(3, chatClusterId: 4, selectedClusterId: 2)));

        // No chat cluster/viewed sector (before a ship exists): selection still wins.
        Assert.Equal(StarmapThreatPresentation.SectorOverlay.Selected,
            StarmapThreatPresentation.OverlayFor(2, chatClusterId: null, selectedClusterId: 2));
    }

    [Fact]
    public void Commander_highlight_flashes_cyan_only_on_the_off_flash_phase()
    {
        IReadOnlySet<short> highlighted = new HashSet<short> { 4, 9 };
        Assert.True(StarmapThreatPresentation.FlashingOutline(highlighted, 4, flashOn: false));
        Assert.True(StarmapThreatPresentation.FlashingOutline(highlighted, 9, flashOn: false));
        Assert.False(StarmapThreatPresentation.FlashingOutline(highlighted, 4, flashOn: true));
        Assert.False(StarmapThreatPresentation.FlashingOutline(highlighted, 3, flashOn: false));
        Assert.False(StarmapThreatPresentation.FlashingOutline(null, 4, flashOn: false));
    }
}
