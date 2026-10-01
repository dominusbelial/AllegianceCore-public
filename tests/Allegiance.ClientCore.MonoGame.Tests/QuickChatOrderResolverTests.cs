using Allegiance.ClientCore.MonoGame.QuickChat;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using SimVec = Allegiance.Core.Collision.Vector3;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure tests for <see cref="QuickChatOrderResolver"/> — the P457 port of
/// native <c>SendQuickChat</c>'s target-resolution half (console.cpp:1534-1600).
/// These pin the per-mask semantics headlessly: the special <c>ttMyTarget</c> /
/// <c>ttMe</c> / <c>ttMyQueued</c> / <c>ttWorstEnemy</c> masks, the family
/// (friendly/enemy × ship/station + ability) nearest rule, and the
/// "no match → cid = c_cidNone" fallback that downgrades a command to voice-only.
/// </summary>
public class QuickChatOrderResolverTests
{
    // ── tiny builders for the resolver's inputs ─────────────────────────────

    private static Game1.CandidateObject Cand(
        Game1.TargetKind kind, short id, SimVec pos, short? side = null, short role = 0)
        => new(pos, kind, id, $"#{id}", side, role);

    private static SimVec V(float x, float y = 0, float z = 0) => new(x, y, z);

    // ── ttMyTarget — the T-selected target ──────────────────────────────────

    [Fact]
    public void AttackMyTarget_UsesTheSelectedTarget()
    {
        var target = Cand(Game1.TargetKind.Ship, 42, V(0, 0, 5), side: 1);
        var selected = new Game1.AutopilotTarget(Game1.TargetKind.Ship, 42, "C");

        var r = QuickChatOrderResolver.Resolve(
            OrderChange.CidAttack, QuickChatOrderResolver.TtMyTarget, 0,
            selfId: 1, mySideId: 0, V(0, 0, 0), selected, null, new[] { target });

        Assert.Equal(OrderChange.CidAttack, r.CommandId);
        Assert.True(r.CarriesOrder);
        Assert.Equal(IgcObjectType.Ship, r.OtTarget);
        Assert.Equal((short)42, r.OidTarget);
    }

    [Fact]
    public void AttackMyTarget_WithNoSelection_YieldsNoOrder()
    {
        var r = QuickChatOrderResolver.Resolve(
            OrderChange.CidAttack, QuickChatOrderResolver.TtMyTarget, 0,
            selfId: 1, mySideId: 0, V(0, 0, 0), selectedTarget: null, null,
            new[] { Cand(Game1.TargetKind.Ship, 42, V(0, 0, 5), side: 1) });

        Assert.Equal(OrderChange.CidNone, r.CommandId);
        Assert.False(r.CarriesOrder);
        Assert.Equal((IgcObjectType)(-1), r.OtTarget);
        Assert.Equal((short)-1, r.OidTarget);
    }

    // ── ttMe — own ship ─────────────────────────────────────────────────────

    [Fact]
    public void FollowMe_ResolvesToOwnShip()
    {
        var r = QuickChatOrderResolver.Resolve(
            OrderChange.CidGoto, QuickChatOrderResolver.TtMe, 0,
            selfId: 7, mySideId: 0, V(0, 0, 0), selectedTarget: null, null, Array.Empty<Game1.CandidateObject>());

        Assert.Equal(OrderChange.CidGoto, r.CommandId);
        Assert.Equal(IgcObjectType.Ship, r.OtTarget);
        Assert.Equal((short)7, r.OidTarget);
    }

    // ── ttWorstEnemy — documented gap: no damage track → none ───────────────

    [Fact]
    public void WorstEnemy_ResolvesToNone_NoDamageTrack()
    {
        var r = QuickChatOrderResolver.Resolve(
            OrderChange.CidAttack, QuickChatOrderResolver.TtWorstEnemy, 0,
            selfId: 1, mySideId: 0, V(0, 0, 0), selectedTarget: null, null,
            new[] { Cand(Game1.TargetKind.Ship, 9, V(0, 0, 3), side: 1) });

        Assert.Equal(OrderChange.CidNone, r.CommandId);
        Assert.False(r.CarriesOrder);
    }

    // ── ttMyQueued / ttMyAccepted — the P424 queued-order state ─────────────

    [Fact]
    public void MyQueued_UsesThePendingQueuedOrdersTarget()
    {
        var queued = new QueuedCommandState(OrderChange.CidGoto, IgcObjectType.Ship, 55);
        var r = QuickChatOrderResolver.Resolve(
            OrderChange.CidGoto, QuickChatOrderResolver.TtMyQueued, 0,
            selfId: 1, mySideId: 0, V(0, 0, 0), selectedTarget: null, queued, Array.Empty<Game1.CandidateObject>());

        Assert.Equal(OrderChange.CidGoto, r.CommandId);
        Assert.Equal(IgcObjectType.Ship, r.OtTarget);
        Assert.Equal((short)55, r.OidTarget);
    }

    [Fact]
    public void MyQueued_WithNoPendingOrder_YieldsNoOrder()
    {
        var clear = new QueuedCommandState(OrderChange.CidNone, (IgcObjectType)(-1), -1);
        var r = QuickChatOrderResolver.Resolve(
            OrderChange.CidGoto, QuickChatOrderResolver.TtMyQueued, 0,
            selfId: 1, mySideId: 0, V(0, 0, 0), selectedTarget: null, clear, Array.Empty<Game1.CandidateObject>());

        Assert.Equal(OrderChange.CidNone, r.CommandId);
        Assert.False(r.CarriesOrder);
    }

    // ── Voice-only leaf (cid = c_cidNone) stays voice-only ─────────────────

    [Fact]
    public void VoiceOnly_Leaf_StaysVoiceOnly()
    {
        var r = QuickChatOrderResolver.Resolve(
            OrderChange.CidNone, 0, 0,
            selfId: 1, mySideId: 0, V(0, 0, 0), selectedTarget: null, null, Array.Empty<Game1.CandidateObject>());

        Assert.Equal(OrderChange.CidNone, r.CommandId);
        Assert.False(r.CarriesOrder);
    }

    // ── Family masks: friendly/enemy × ship/station + ability, nearest ─────

    [Fact]
    public void Family_NearestEnemyShip_IgnoresCloserFriendly()
    {
        var enemyFar = Cand(Game1.TargetKind.Ship, 10, V(0, 0, 100), side: 1);
        var enemyNear = Cand(Game1.TargetKind.Ship, 11, V(0, 0, 10), side: 1);
        var friendlyNear = Cand(Game1.TargetKind.Ship, 12, V(0, 0, 3), side: 0);   // closest but friendly

        var r = QuickChatOrderResolver.Resolve(
            OrderChange.CidAttack,
            QuickChatOrderResolver.TtEnemy | QuickChatOrderResolver.TtShip | QuickChatOrderResolver.TtNearest,
            0, selfId: 1, mySideId: 0, V(0, 0, 0), selectedTarget: null, null,
            new[] { enemyFar, enemyNear, friendlyNear });

        Assert.Equal(OrderChange.CidAttack, r.CommandId);
        Assert.Equal(IgcObjectType.Ship, r.OtTarget);
        Assert.Equal((short)11, r.OidTarget);   // nearest ENEMY, not the nearer friendly
    }

    [Fact]
    public void Family_FriendlyStation_PicksNearestStation()
    {
        var st1 = Cand(Game1.TargetKind.Station, 700, V(0, 0, 50), side: 0);
        var st2 = Cand(Game1.TargetKind.Station, 701, V(0, 0, 12), side: 0);

        var r = QuickChatOrderResolver.Resolve(
            OrderChange.CidDefend,
            QuickChatOrderResolver.TtFriendly | QuickChatOrderResolver.TtStation | QuickChatOrderResolver.TtNearest,
            0, selfId: 1, mySideId: 0, V(0, 0, 0), selectedTarget: null, null, new[] { st1, st2 });

        Assert.Equal(OrderChange.CidDefend, r.CommandId);
        Assert.Equal(IgcObjectType.Station, r.OtTarget);
        Assert.Equal((short)701, r.OidTarget);
    }

    [Fact]
    public void Family_PrefersSelectedTarget_WhenItMatchesTheMask()
    {
        var farFriend = Cand(Game1.TargetKind.Ship, 20, V(0, 0, 999), side: 0);
        var selFriend = Cand(Game1.TargetKind.Ship, 21, V(0, 0, 40), side: 0);
        var selected = new Game1.AutopilotTarget(Game1.TargetKind.Ship, 21, "wingman");

        var r = QuickChatOrderResolver.Resolve(
            OrderChange.CidDefend,
            QuickChatOrderResolver.TtFriendly | QuickChatOrderResolver.TtShip | QuickChatOrderResolver.TtNearest,
            0, selfId: 1, mySideId: 0, V(0, 0, 0), selected, null, new[] { farFriend, selFriend });

        Assert.Equal((short)21, r.OidTarget);   // the SELECTED friendly, not the nearest family fallback
    }

    [Fact]
    public void Family_AbilityMaskFiltersByRoleBits()
    {
        var plain = Cand(Game1.TargetKind.Ship, 30, V(0, 0, 3), side: 1, role: 0);
        var fighter = Cand(Game1.TargetKind.Ship, 31, V(0, 0, 20), side: 1, role: 0x80);   // habmFighter

        var r = QuickChatOrderResolver.Resolve(
            OrderChange.CidAttack,
            QuickChatOrderResolver.TtEnemy | QuickChatOrderResolver.TtShip | QuickChatOrderResolver.TtNearest,
            0x80 /*habmFighter*/, selfId: 1, mySideId: 0, V(0, 0, 0), selectedTarget: null, null, new[] { plain, fighter });

        Assert.Equal((short)31, r.OidTarget);   // the closest one WITHOUT the ability is excluded
    }

    [Fact]
    public void Family_NoMatch_YieldsCidNone()
    {
        var onlyEnemy = Cand(Game1.TargetKind.Ship, 40, V(0, 0, 3), side: 1);
        var r = QuickChatOrderResolver.Resolve(
            OrderChange.CidDefend,
            QuickChatOrderResolver.TtFriendly | QuickChatOrderResolver.TtShip | QuickChatOrderResolver.TtNearest,
            0, selfId: 1, mySideId: 0, V(0, 0, 0), selectedTarget: null, null, new[] { onlyEnemy });

        Assert.Equal(OrderChange.CidNone, r.CommandId);
        Assert.False(r.CarriesOrder);
    }

    // ── cidMyAccepted degrades to none (no accepted-command state client-side) ──

    [Fact]
    public void CidMyAccepted_DegradesToNone()
    {
        var r = QuickChatOrderResolver.Resolve(
            QuickChatOrderResolver.CidMyAccepted, QuickChatOrderResolver.TtMyAccepted, 0,
            selfId: 1, mySideId: 0, V(0, 0, 0), selectedTarget: null, null, Array.Empty<Game1.CandidateObject>());

        Assert.Equal(OrderChange.CidNone, r.CommandId);
        Assert.False(r.CarriesOrder);
    }
}
