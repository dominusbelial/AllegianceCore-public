using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

[Trait("Tier", "Fast")]
public sealed class P1071BackfillTests
{
    private static BackfillPilot Bot(string name, short side = 0, bool protect = false, string role = "fighter", bool owned = true)
        => new(name, side, true, protect, role, owned);
    private static BackfillPilot Human(string name, short side = 0) => new(name, side, false, false, "human", false);

    [Fact]
    public void Seats_replace_combat_and_refill_after_thirty_seconds_with_reconnect_cancellation()
    {
        var policy = new BackfillSeats(3);
        BackfillPilot[] initial = [Bot("mcp-cmd", protect: true), Bot("mcp-1"), Bot("mcp-2")];
        Assert.Null(policy.Decide(0, initial, 0));
        var joined = initial.Append(Human("Pilot" )).ToArray();
        Assert.Equal(new(0, 1, 3, "quit", "mcp-1"), policy.Decide(0, joined, 1));
        var balanced = joined.Where(p => p.Name != "mcp-1").ToArray();
        Assert.Null(policy.Decide(0, balanced, 2));
        var left = balanced.Where(p => p.Bot).ToArray();
        Assert.Null(policy.Decide(0, left, 10));
        Assert.Null(policy.Decide(0, left, 39.99));
        Assert.Equal("join", policy.Decide(0, left, 40)!.Action);
        Assert.Null(policy.Decide(0, balanced, 41)); // reconnect cancels outstanding deficit
        Assert.Null(policy.Decide(0, left, 42));
        Assert.Null(policy.Decide(0, left, 71.99));
        Assert.Equal("join", policy.Decide(0, left, 72)!.Action);
    }

    [Fact]
    public void Drop_order_excludes_commanders_economy_external_bots_and_humans()
    {
        BackfillPilot[] roster = [Bot("mcp-cmd", protect: true), Bot("mcp-miner", protect: true, role: "miner"),
            Bot("mcp-outsider", owned: false), Human("Pilot"), Bot("mcp-scout", role: "scout"), Bot("mcp-fighter")];
        Assert.Equal(new[] { "mcp-fighter", "mcp-scout" }, BackfillSeats.DropOrder(roster).Select(p => p.Name));
        Assert.Equal("protected", new BackfillSeats(1).Decide(0, roster[..4], 0)!.Action);
        Assert.Null(new BackfillSeats(3).Decide(0, [Human("A"), Human("B"), Human("C")], 0));
        Assert.Equal("protected", new BackfillSeats(2).Decide(0, [Human("A"), Human("B"), Human("C")], 0)!.Action);
    }

    [Fact]
    public void New_match_preserves_human_seats_and_iterates_arbitrary_side_ids()
    {
        var policy = new BackfillSeats(3);
        BackfillPilot[] roster = [Human("A", 4), Bot("mcp-1", 4), Bot("mcp-cmd4", 4, true), Bot("mcp-cmd7", 7, true)];
        Assert.Null(policy.Decide(4, roster, 0));
        Assert.Equal(new(7, 0, 1, "join"), policy.Decide(7, roster, 0));
        policy.NewMatch();
        Assert.Null(policy.Decide(4, roster, 5));
        Assert.Equal("join", policy.Decide(7, roster, 5)!.Action);
    }

    [Fact]
    public void Human_departure_during_new_match_settle_keeps_grace_but_empty_bot_seats_fill_immediately()
    {
        var policy = new BackfillSeats(3);
        BackfillPilot[] seated = [Human("Pilot"), Bot("mcp-cmd", protect: true), Bot("mcp-combat")];
        Assert.Null(policy.Decide(0, seated, 0));
        Assert.Equal("join", policy.Decide(7, [Bot("mcp-cmd7", 7, true)], 0)!.Action);
        policy.NewMatch();
        var leftDuringSettle = seated.Where(p => p.Bot).ToArray();
        Assert.Null(policy.Decide(0, leftDuringSettle, 10));
        Assert.Null(policy.Decide(0, leftDuringSettle, 39.99));
        Assert.Equal("join", policy.Decide(0, leftDuringSettle, 40)!.Action);
        Assert.Equal("join", policy.Decide(7, [Bot("mcp-cmd7", 7, true)], 10)!.Action);
    }

    [Fact]
    public void Driver_flag_is_default_off_and_validated()
    {
        Assert.Null(DriverOptions.Parse(["localhost", "6073"]).BackfillSeats);
        Assert.Equal(5, DriverOptions.Parse(["localhost", "6073", "--backfill-seats", "5"]).BackfillSeats);
        Assert.Equal("2.0.0", DriverOptions.Parse(["localhost", "6073", "--backfill-seats", "5", "--client-version", "2.0.0"]).ClientVersion);
        Assert.Throws<ArgumentException>(() => DriverOptions.Parse(["localhost", "6073", "--backfill-seats", "5", "--client-version", "unknown"]));
        Assert.Throws<ArgumentException>(() => DriverOptions.Parse(["localhost", "6073", "--backfill-seats", "0"]));
        Assert.Throws<ArgumentException>(() => DriverOptions.Parse(["localhost", "6073", "--backfill-seats", "5", "--workers", "2"]));
        Assert.Throws<ArgumentException>(() => DriverOptions.Parse(["localhost", "6073", "--backfill-seats", "5", "--bot-names", "*"]));
    }
}
