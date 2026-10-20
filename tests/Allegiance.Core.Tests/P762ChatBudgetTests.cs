using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Transport;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Core.Tests;

/// <summary>
/// P762 — SIM-0145, the native chat budget's exact charge/debt semantics
/// (FedSrv/fsship.h:24-25,449-468) and the selectable native profile
/// (100/20/+1) alongside the P654 dedicated defaults (1000/20/+5).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P762ChatBudgetTests
{
    private static Cfg Config(string? profile = null) => new()
    {
        ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
        Transport = profile is null ? null : new TransportConfig { ChatBudgetProfile = profile },
        Games = [new Gm { Name = "P762 budget", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] }],
    };

    private static T Field<T>(Srv server, string name) => (T)typeof(Srv)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;

    private static bool Charge(Srv server, ConnectionId peer, bool pay = true) =>
        (bool)typeof(Srv).GetMethod("TryChargeChatBudget", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [peer, pay])!;

    private static int Budget(Srv server, ConnectionId peer) =>
        ((Dictionary<ConnectionId, (int Budget, long LastRefillTick)>)typeof(Srv)
            .GetField("_chatBudgets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!)[peer].Budget;

    private static void SetBudget(Srv server, ConnectionId peer, int value)
    {
        var dict = (Dictionary<ConnectionId, (int Budget, long LastRefillTick)>)typeof(Srv)
            .GetField("_chatBudgets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        dict[peer] = (value, 0);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Default_and_native_profiles_are_selectable()
    {
        Require.Artwork();
        using (var dedicated = new Srv())
        {
            dedicated.Boot(Config());
            Assert.Equal(1000, Field<int>(dedicated, "_chatBudgetMax"));
            Assert.Equal(20, Field<int>(dedicated, "_chatBudgetCost"));
            Assert.Equal(5, Field<int>(dedicated, "_chatBudgetRefillPerTick"));
        }
        using (var native = new Srv())
        {
            native.Boot(Config("native"));
            Assert.Equal(100, Field<int>(native, "_chatBudgetMax"));
            Assert.Equal(20, Field<int>(native, "_chatBudgetCost"));
            Assert.Equal(1, Field<int>(native, "_chatBudgetRefillPerTick"));
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Paid_sends_charge_and_floor_at_negative_cost_while_unpaid_sends_only_test_nonnegative()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config("native"));
        var peer = new ConnectionId(42);

        Assert.True(Charge(server, peer)); Assert.Equal(80, Budget(server, peer));
        Assert.True(Charge(server, peer)); Assert.Equal(60, Budget(server, peer));
        Assert.True(Charge(server, peer)); Assert.Equal(40, Budget(server, peer));
        Assert.True(Charge(server, peer)); Assert.Equal(20, Budget(server, peer));
        Assert.True(Charge(server, peer)); Assert.Equal(0, Budget(server, peer));

        // A denied paid send still subtracts the cost, floored at -cost.
        Assert.False(Charge(server, peer)); Assert.Equal(-20, Budget(server, peer));
        Assert.False(Charge(server, peer)); Assert.Equal(-20, Budget(server, peer));

        // Unpaid (group-noecho) requires >= 0 and never changes the budget.
        Assert.False(Charge(server, peer, pay: false)); Assert.Equal(-20, Budget(server, peer));
        SetBudget(server, peer, 0);
        Assert.True(Charge(server, peer, pay: false)); Assert.Equal(0, Budget(server, peer));
        SetBudget(server, peer, -1);
        Assert.False(Charge(server, peer, pay: false)); Assert.Equal(-1, Budget(server, peer));
    }
}
