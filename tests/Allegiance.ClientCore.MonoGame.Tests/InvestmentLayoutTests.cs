using System.Collections.Generic;
using Allegiance.Core.Model;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P487 — pure coverage of <see cref="InvestmentLayout"/>, the ASCII-safe row
/// stream the investment pane draws (through the same <see cref="DockedScreenRenderer"/>
/// the shop uses): a title/hint header, the money line, per-kind group section
/// headers in synthesis order, one item row per bucket with price / invested /
/// progress / remaining build time, and the empty-list hint.
/// </summary>
public class InvestmentLayoutTests
{
    private static InvestmentBucket Dev(short id, int price, uint ttb = 30) =>
        new(id, BucketItemType.BitDevelopment, $"dev{id}", price, ttb, -1, -1, id, -1);

    private static InvestmentPaneModel Model(IReadOnlyList<InvestmentBucket> defs, int? myMoney = 5000,
        IReadOnlyDictionary<short, ClientWorld.BucketFunding>? funding = null) =>
        new(defs, funding, missionInProgress: true, hasSide: true, myMoney: myMoney);

    [Fact]
    public void Build_EmitsTitleHintMoneyAndGroupHeaders()
    {
        var m = Model([Dev(2, 1000), Dev(3, 1500)]);
        var rows = InvestmentLayout.Build(m);

        Assert.Contains(rows, r => r.Kind == ShopLayout.Kind.Title && r.Text.Contains("TEAM INVESTMENT"));
        Assert.Contains(rows, r => r.Kind == ShopLayout.Kind.Hint && r.Text.Contains("INVEST"));
        Assert.Contains(rows, r => r.Kind == ShopLayout.Kind.Info && r.Text.Contains("money: $5000"));
        Assert.Contains(rows, r => r.Kind == ShopLayout.Kind.Section && r.Text == "DEVELOPMENTS");

        // one item row per development bucket, showing price + progress + time-left
        Assert.Equal(2, CountKind(rows, ShopLayout.Kind.Item));
        Assert.Contains(rows, r => r.Kind == ShopLayout.Kind.Item && r.Text.Contains("$1000"));
        Assert.Contains(rows, r => r.Kind == ShopLayout.Kind.Item && r.Text.Contains("0%"));
    }

    [Fact]
    public void Build_InsertsGroupHeaders_InSynthesisOrder()
    {
        var m = Model([
            new InvestmentBucket(1, BucketItemType.BitShipType, "bomber", 5800, 30, 504, -1, -1, -1),
            Dev(2, 1000),
            new InvestmentBucket(3, BucketItemType.BitDroneType, "drone", 1500, 40, 501, 3, -1, -1),
            new InvestmentBucket(4, BucketItemType.BitStationType, "garrison", 2000, 60, -1, -1, -1, 7),
        ]);
        var rows = InvestmentLayout.Build(m);

        Assert.Contains(rows, r => r.Kind == ShopLayout.Kind.Section && r.Text == "SHIPS & DRONES");
        Assert.Contains(rows, r => r.Kind == ShopLayout.Kind.Section && r.Text == "DEVELOPMENTS");
        Assert.Contains(rows, r => r.Kind == ShopLayout.Kind.Section && r.Text == "DRONES");
        Assert.Contains(rows, r => r.Kind == ShopLayout.Kind.Section && r.Text == "STATIONS");

        // group headers appear in the same order as their first item
        var sectionOrder = new List<string>();
        foreach (var r in rows)
            if (r.Kind == ShopLayout.Kind.Section) sectionOrder.Add(r.Text);
        Assert.Equal(["SHIPS & DRONES", "DEVELOPMENTS", "DRONES", "STATIONS"], sectionOrder);
    }

    [Fact]
    public void Build_EscapesUnicodeNames_ToPrintableAscii()
    {
        // Native core names occasionally carry characters outside printable ASCII;
        // the HUD spritefonts bake ASCII 32..126 only, so the layout must strip.
        var b = Dev(2, 1000) with { Name = "dev\u00e9\u2020" };   // é + dagger
        var rows = InvestmentLayout.Build(Model([b]));
        var item = rows.FirstOrDefault(r => r.Kind == ShopLayout.Kind.Item)!;
        Assert.DoesNotContain("\u00e9", item.Text);
        Assert.DoesNotContain("\u2020", item.Text);
    }

    [Fact]
    public void Build_EmptyList_ShowsHint()
    {
        var m = Model([]);
        var rows = InvestmentLayout.Build(m);
        Assert.Contains(rows, r => r.Kind == ShopLayout.Kind.Muted && r.Text.Contains("no fundable buckets"));
    }

    [Fact]
    public void Build_NonInvestableRow_IsGreyed_WhenVisibleViaShowAll()
    {
        var b = Dev(2, 1000);
        // no-money makes the bucket uninvestable; ShowAll keeps it visible.
        var m = new InvestmentPaneModel([b], null, missionInProgress: true, hasSide: true, myMoney: 0,
            showAll: true, hideCompleted: false);
        var rows = InvestmentLayout.Build(m);
        var item = rows.FirstOrDefault(r => r.Kind == ShopLayout.Kind.Item && r.Text.Contains("dev2"))!;
        Assert.True(item.Greyed);
    }

    private static int CountKind(IReadOnlyList<ShopLayout.Row> rows, ShopLayout.Kind kind)
    {
        int n = 0;
        foreach (var r in rows) if (r.Kind == kind) n++;
        return n;
    }
}
