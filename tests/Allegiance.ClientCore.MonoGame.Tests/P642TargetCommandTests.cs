using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;
using Xunit;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class P642TargetCommandTests
{
    private static Game1.CandidateObject Object(short id, float distance, Game1.TargetKind kind = Game1.TargetKind.Warp) =>
        new(new SimVec(distance, 0, 0), kind, id, $"object {id}");
    private static readonly Game1.CandidateObject[] Candidates = [Object(1, 300), Object(2, 100), Object(3, 200)];
    [Theory]
    [InlineData((int)Game1.StepDirection.Nearest, 2)]
    [InlineData((int)Game1.StepDirection.Next, 1)]
    [InlineData((int)Game1.StepDirection.Previous, 2)]
    public void Direction_uses_native_list_order_and_nearest_distance(int direction, short expected)
    {
        var current = new Game1.AutopilotTarget(Game1.TargetKind.Warp, 3, "current");
        Assert.Equal(expected, Game1.ChooseTarget(Candidates, SimVec.Zero, current, (Game1.StepDirection)direction)!.Value.Id);
    }
    [Theory]
    [InlineData((int)Game1.StepDirection.Nearest)]
    [InlineData((int)Game1.StepDirection.Next)]
    [InlineData((int)Game1.StepDirection.Previous)]
    public void Missing_current_uses_nearest_and_empty_pool_clears(int direction)
    {
        Assert.Equal(2, Game1.ChooseTarget(Candidates, SimVec.Zero, new(Game1.TargetKind.Ship, 2, "same id different type"), (Game1.StepDirection)direction)!.Value.Id);
        Assert.Null(Game1.ChooseTarget([], SimVec.Zero, null, (Game1.StepDirection)direction));
    }
    [Fact]
    public void Equal_distances_keep_model_order()
    {
        Assert.Equal(5, Game1.ChooseTarget([Object(5, 100), Object(1, 100)], SimVec.Zero, null, Game1.StepDirection.Nearest)!.Value.Id);
    }
    private static TreasureEntity Treasure(short id, short sector, short payload, TreasureCode code = TreasureCode.Flag) =>
        new() { TreasureId = id, ClusterId = sector, PayloadId = payload, Code = code, Position = new(id * 100, 0, 0) };
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    public void Flags_and_artifacts_use_flag_code_owner_and_sector_not_part_or_cash(bool artifact, short expected)
    {
        var treasures = new[] { Treasure(1, 1, 0), Treasure(2, 1, 1), Treasure(3, 1, -1), Treasure(4, 2, 1),
            Treasure(5, 1, -1, TreasureCode.Part), Treasure(6, 1, -1, TreasureCode.Cash), Treasure(7, 1, 2) }.ToDictionary(t => t.TreasureId);
        var candidates = Game1.GatherTreasureTargets(treasures, 1, 0, artifact, (a, b) => b == 2);
        Assert.Equal(expected, Assert.Single(candidates).Id);
        Assert.Equal(Game1.TargetKind.Treasure, candidates[0].Kind);
        treasures.Remove(expected);
        Assert.Empty(Game1.GatherTreasureTargets(treasures, 1, 0, artifact, (a, b) => b == 2));
    }
    [Fact]
    public void Unknown_own_side_does_not_turn_every_flag_into_an_enemy()
    {
        Assert.Empty(Game1.GatherTreasureTargets(new Dictionary<short, TreasureEntity> { [1] = Treasure(1, 1, 2) }, 1, null, false));
    }
    [Theory]
    [InlineData("TargetFriendlyBase", false, 0, true)]
    [InlineData("TargetFriendlyBase", false, 1, false)]
    [InlineData("TargetFriendlyBase", true, 1, true)]
    [InlineData("TargetAlliedBase", false, 0, false)]
    [InlineData("TargetAlliedBase", false, 1, true)]
    [InlineData("TargetAlliedBase", false, 2, false)]
    [InlineData("TargetEnemyBase", false, 0, false)]
    [InlineData("TargetEnemyBase", false, 1, false)]
    [InlineData("TargetEnemyBase", false, 2, true)]
    public void Allied_base_excludes_own_side_when_allied_and_pod_friendly_includes_allies(string family, bool pod, short candidate, bool expected)
    {
        var world = new ClientWorld();
        world.Apply(new ChangeAlliances { Allies = [0, 0, -1, -1, -1, -1] });
        Assert.Equal(expected, Game1.BaseRelationMatches(world, 0, candidate, family, pod));
    }
    [Fact]
    public void Without_allies_allied_base_uses_native_friendly_fallback()
    {
        var world = new ClientWorld();
        Assert.True(Game1.BaseRelationMatches(world, 0, 0, "TargetAlliedBase", false));
        Assert.False(Game1.BaseRelationMatches(world, 0, 1, "TargetAlliedBase", false));
    }
    [Theory]
    [InlineData(-1, null)]
    [InlineData(17, 17)]
    [InlineData(0, 0)]
    public void Occupy_next_only_requests_current_parent(short parent, int? expected) => Assert.Equal(expected, (int?)Game1.NextTurretParent(parent));

    private static DataWarpIGC Warp(short id, short destination, short sector, float x) =>
        new(id, destination, 100, "", "", $"warp {id}", [x, 0, 0], [0, 0, 1], [0, 0, 0], 1, sector);
    [Fact]
    public void Any_cluster_search_prefers_local_then_fewest_jumps_and_nearest_arrival_mouth()
    {
        var warps = new[] { Warp(1, 2, 1, 0), Warp(2, 1, 2, 1000), Warp(3, 4, 2, 0), Warp(4, 3, 3, 0) }.ToDictionary(w => w.WarpId);
        Game1.CandidateObject[] objects = [Object(1, 90000, Game1.TargetKind.Station), Object(2, 0, Game1.TargetKind.Station),
            Object(3, 990, Game1.TargetKind.Station), Object(4, 0, Game1.TargetKind.Station), Object(5, 1, Game1.TargetKind.Station)];
        short Sector(Game1.CandidateObject c) => c.Id == 1 ? (short)1 : c.Id is 2 or 3 ? (short)2 : c.Id == 4 ? (short)3 : (short)4;
        Assert.Equal(1, Game1.ChooseAcrossClusters(objects, Sector, warps, 1, SimVec.Zero, null, Game1.StepDirection.Nearest)!.Value.Id);
        Assert.Equal(3, Game1.ChooseAcrossClusters(objects[1..], Sector, warps, 1, SimVec.Zero, null, Game1.StepDirection.Nearest)!.Value.Id);
        Assert.Equal(4, Game1.ChooseAcrossClusters(objects[3..], Sector, warps, 1, SimVec.Zero, null, Game1.StepDirection.Nearest)!.Value.Id);
        Assert.Null(Game1.ChooseAcrossClusters(objects[4..], Sector, warps, 1, SimVec.Zero, null, Game1.StepDirection.Nearest));
    }
    [Fact]
    public void Previous_reverses_warp_search_order_without_skipping_nearer_clusters()
    {
        var warps = new[] { Warp(1, 2, 1, 0), Warp(2, 1, 2, 0), Warp(3, 4, 1, 0), Warp(4, 3, 3, 0) }.ToDictionary(w => w.WarpId);
        Game1.CandidateObject[] objects = [Object(2, 100), Object(3, 100)];
        short Sector(Game1.CandidateObject c) => c.Id;
        Assert.Equal(2, Game1.ChooseAcrossClusters(objects, Sector, warps, 1, SimVec.Zero, null, Game1.StepDirection.Next)!.Value.Id);
        Assert.Equal(3, Game1.ChooseAcrossClusters(objects, Sector, warps, 1, SimVec.Zero, null, Game1.StepDirection.Previous)!.Value.Id);
    }
    [Theory]
    [InlineData("TargetEnemy", 0, 1, false)]
    [InlineData("TargetEnemy", DataHullTypeIGC.HabmMiner, 1, true)]
    [InlineData("TargetEnemy", DataHullTypeIGC.HabmMiner, 2, false)]
    [InlineData("TargetFriendly", 0, 2, true)]
    [InlineData("TargetFriendly", 0, 0, true)]
    [InlineData("TargetFriendly", 0, 1, false)]
    public void Generic_enemy_ship_filter_uses_native_utility_hulls_and_excludes_allies(string family, short role, short side, bool expected)
    {
        var world = new ClientWorld();
        world.Apply(new ChangeAlliances { Allies = [0, -1, 0, -1, -1, -1] });
        Assert.Equal(expected, Game1.ShipRelationMatches(world, 0, new(SimVec.Zero, Game1.TargetKind.Ship, 9, "ship", side, role), family));
    }

    public static IEnumerable<object[]> Actions => Game1.RemainderTargetActions.Select(a => new object[] { a.Id });
    [Theory]
    [MemberData(nameof(Actions))]
    public void Every_target_action_has_TK_identity_help_and_persistent_rebinding(string action)
    {
        var definition = Assert.Single(InputActionCatalog.Definitions, d => d.Name == action);
        Assert.Contains("TK_" + action, definition.Description);
        var map = new ActionMap(InputActionCatalog.Definitions);
        map.SetBinding(action, new(Keys.F24, InputModifiers.None), force: true);
        Assert.True(map.IsDown(new KeyboardState(Keys.F24), action));
        var restored = new ActionMap(InputActionCatalog.Definitions);
        restored.ApplyJson(map.ToJson());
        Assert.True(restored.IsDown(new KeyboardState(Keys.F24), action));
        Assert.Contains("F24", restored.BindingOf(action).Label);
    }
}
