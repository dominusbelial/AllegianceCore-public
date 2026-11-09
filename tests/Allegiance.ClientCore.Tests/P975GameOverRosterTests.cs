using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P975 (P971-06) — the game-over player roster keeps ONE row per mission player.
///
/// Native's client does not accumulate GAME_OVER_PLAYERS(63) arrivals: it walks the
/// chunk and <c>FindPlayer(name)</c> → <c>UpdateScore(stats)</c> for each record, so
/// a repeated or duplicate record updates the same player and an unknown name is
/// ignored (<c>clintlib/appmsg.cpp:3094-3110</c>, count from <c>cbrgPlayerInfo</c> at
/// <c>:3099</c>). The server sends the roster in chunks of at most 50 players
/// (<c>FedSrv/fsmission.cpp:3132-3141</c>, <c>nMaxPlayersPerMsg = 50</c>), queued
/// from the end of the roster.
///
/// Before the fix, <c>ClientWorld.Apply(GameOverPlayers)</c> did
/// <c>[.. _gameOverPlayers, .. players.Entries]</c>: every frame appended, duplicates
/// included, and recopied the whole list — unbounded retention and O(N²) copying
/// from a hostile or merely restarting server, and the game-over screen then drew
/// thousands of duplicate rows.
/// </summary>
public class P975GameOverRosterTests
{
    private static GameOverPlayerEntry Entry(string name, short side, float score, short kills = 0)
        => new(name, side, 0, score, Kills: kills);

    /// <summary>Native's chunking: 50 players per message, queued from the end of
    /// the roster (fsmission.cpp:3131-3141), so the client sees reversed chunks.</summary>
    private static List<GameOverPlayers> NativeChunks(IReadOnlyList<GameOverPlayerEntry> roster)
    {
        var messages = new List<GameOverPlayers>();
        var entries = roster.ToArray();
        for (int remaining = entries.Length; remaining > 0;)
        {
            int count = Math.Min(remaining, 50);
            remaining -= count;
            messages.Add(new GameOverPlayers { Entries = entries[remaining..(remaining + count)] });
        }
        return messages;
    }

    private static GameOverPlayerEntry[] TwoHundredPlayers()
        => Enumerable.Range(0, 100)
            .Select(i => Entry($"pilot-{i:00}", (short)(i % 2), 1_000f + i, (short)(i % 7)))
            .ToArray();

    [Fact]
    public void NativeChunks_ProduceOneRowPerPlayer_AndDuplicatesDoNotGrowTheRoster()
    {
        var roster = TwoHundredPlayers();
        var world = new ClientWorld();
        world.Apply(new GameOver { SideWinner = 0 });

        foreach (var chunk in NativeChunks(roster)) world.Apply(chunk);
        Assert.Equal(100, world.GameOverPlayersList.Count);
        Assert.Equal(100, world.GameOverPlayersList.Select(e => e.CharacterName).Distinct().Count());

        // A duplicate frame (the same chunk again, and the whole roster again) must
        // update the existing rows, never append — native FindPlayer+UpdateScore.
        var afterFirst = world.GameOverPlayersList;
        foreach (var chunk in NativeChunks(roster)) world.Apply(chunk);
        Assert.Equal(100, world.GameOverPlayersList.Count);
        Assert.Same(afterFirst, world.GameOverPlayersList);   // no whole-list recopy per frame

        for (int frame = 0; frame < 200; frame++) world.Apply(NativeChunks(roster)[0]);
        Assert.Equal(100, world.GameOverPlayersList.Count);
        Assert.Same(afterFirst, world.GameOverPlayersList);
    }

    [Fact]
    public void ARepeatedPlayerRecord_UpdatesItsRow_LastValueWins()
    {
        var world = new ClientWorld();
        world.Apply(new GameOver { SideWinner = 0 });
        world.Apply(new GameOverPlayers { Entries = [Entry("alpha", 0, 10f, 1), Entry("beta", 1, 20f, 2)] });
        Assert.Equal(2, world.GameOverPlayersList.Count);

        // Native's UpdateScore: the same player arriving again replaces the score,
        // it does not mint a second row (appmsg.cpp:3105-3108).
        world.Apply(new GameOverPlayers { Entries = [Entry("alpha", 0, 55f, 9), Entry("alpha", 0, 57f, 10)] });
        Assert.Equal(2, world.GameOverPlayersList.Count);
        var alpha = world.GameOverPlayersList.Single(e => e.CharacterName == "alpha");
        Assert.Equal(57f, alpha.Score);
        Assert.Equal(10, alpha.Kills);
        Assert.Equal(20f, world.GameOverPlayersList.Single(e => e.CharacterName == "beta").Score);
    }

    [Fact]
    public void HostileDistinctNames_AreBounded()
    {
        // The roster is bounded by the mission's player slots; native advertises
        // nMaxPlayers < 1000 (IntroFindServerList.MaxListedMaxPlayers, native's
        // `nMaxPlayers < 1000` listing rule), so the port keeps at most that many
        // rows rather than growing without bound.
        var world = new ClientWorld();
        world.Apply(new GameOver { SideWinner = 0 });
        var flood = Enumerable.Range(0, 5000)
            .Select(i => Entry($"intruder-{i}", 0, i))
            .ToArray();
        world.Apply(new GameOverPlayers { Entries = flood });
        Assert.True(world.GameOverPlayersList.Count <= 999,
            $"a hostile GAME_OVER_PLAYERS frame grew the roster to {world.GameOverPlayersList.Count} rows");

        world.Apply(new GameOverPlayers { Entries = flood });
        Assert.True(world.GameOverPlayersList.Count <= 999);
        // The cap is a deliberate drop, counted: the roster never silently loses
        // rows to a hostile frame without evidence of it (new in P975).
        var dropped = typeof(ClientWorld).GetProperty("GameOverPlayersDropped");
        if (dropped?.GetValue(world) is long n)
            Assert.True(n > 0, "records past the roster cap must be counted as dropped");
    }

    [Fact]
    public void GameOverScreenShowsTheSameRowsAsTodaysAccumulation_ForATwoChunkGame()
    {
        var roster = TwoHundredPlayers();
        var world = new ClientWorld();
        world.Apply(new GameOver { SideWinner = 0 });
        foreach (var chunk in NativeChunks(roster)) world.Apply(chunk);

        // The screen builds its player table from the roster (Game1.Rendering.cs
        // calls BuildPlayers(world.GameOverPlayersList, …)). "Same content as today"
        // means the identical arrival-ordered rows, once each: native queues chunks
        // from the END of the roster, so the arrival order is chunk-by-chunk, not
        // the roster order.
        string[] names = world.GameOverPlayersList.Select(e => e.CharacterName).ToArray();
        var expected = NativeChunks(roster).SelectMany(c => c.Entries)
            .Select(e => e.CharacterName).ToArray();
        Assert.Equal(expected, names);

        static string Rank(short r) => $"rank-{r}";
        static short Civ(short side) => side;
        var rows = GameOverTablesLayout.BuildPlayers(
            world.GameOverPlayersList, Rank, Civ, new HashSet<string>(), ejectPods: false,
            new GameOverTablesLayout.PlayerSort(GameOverTablesLayout.PlayerColumn.Position, false));
        Assert.Equal(world.GameOverPlayersList.Count, rows.Count);
        Assert.Equal(Enumerable.Range(1, world.GameOverPlayersList.Count), rows.Select(r => r.Position));
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(world.GameOverPlayersList[i].CharacterName, rows[i].Name);
            Assert.Equal(world.GameOverPlayersList[i].SideId, rows[i].SideId);
            Assert.Equal((int)world.GameOverPlayersList[i].Score, rows[i].Score);
        }
    }
}
