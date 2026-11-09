using Allegiance.Tools.McpSpawner;
using Xunit;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P973 — P798's fog audit rule, pinned in its pure form.
///
/// <para><c>get_side_intel</c> keeps ONE row per (<c>Mission.Cookie</c>, side):
/// bot worlds are shared per side <i>and</i> mission (<c>SharedBotWorlds.Get(cookie)</c>,
/// <c>ClientWorld.AttachBotWorld</c>) and the server hands every match a fresh
/// cookie (<c>Server.Lifecycle.cs</c>: <c>mission.Cookie = ++_nextMissionCookie</c>).
/// A constructor build therefore has to be judged against the row of the match it
/// happened in. Reading "the last row for this side" (what
/// <c>P798FullLoopLiveTests</c> used to do) compared a first-match build with the
/// next match's few-hundred-millisecond-old knowledge and reported a knowledge
/// leak that never happened.</para>
/// </summary>
public sealed class P973FogAuditTests
{
    [Fact]
    public void Intel_is_per_match_so_a_build_is_audited_against_its_own_match()
    {
        var store = new SideIntelStore();
        const short side = 0;
        // Match 1: the side has discovered the rock the constructor later builds on.
        var firstMatch = store.Get(cookie: 1, side);
        lock (store.Sync) firstMatch.ObserveRock(new RockSighting(12, 2080, 4000, 5000, 0x08), 0);
        // Match 2 (fresh cookie, fresh per-side world): only the new match's rocks.
        var nextMatch = store.Get(cookie: 2, side);
        lock (store.Sync) nextMatch.ObserveRock(new RockSighting(91, 2080, 4000, 5000, 0x08), 0);

        var rows = store.ReadAll(all => all.OrderBy(r => r.Cookie).ToArray());
        Assert.Equal(2, rows.Length);                 // one row per (match, side), never one per side
        Assert.Equal(1u, rows[0].Cookie);
        Assert.Equal(2u, rows[1].Cookie);
        Assert.True(rows[0].Rocks.ContainsKey(12));    // the build's rock IS discovered in its own match
        Assert.False(rows[1].Rocks.ContainsKey(12));   // the next match knows nothing about it
        Assert.True(rows[1].Rocks.ContainsKey(91));

        // The audit: select the row by the mission the build happened in.
        Assert.True(rows.Single(r => r.Cookie == 1).Rocks.ContainsKey(12));
        // The old "last row per side" reading is exactly the bug this pins:
        Assert.False(rows[^1].Rocks.ContainsKey(12));
    }
}
