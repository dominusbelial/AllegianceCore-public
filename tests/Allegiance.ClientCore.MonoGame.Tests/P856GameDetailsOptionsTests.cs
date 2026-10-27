using Allegiance.ClientCore.MonoGame;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P856 — the client's GAME DETAILS panel (the port's "Game details" text,
/// native <c>WinTrek/newgamescreen.cpp:327-360</c>) prints the two create-game
/// options native's summary shows: "Allow Friendly Fire: Yes/No"
/// (<c>newgamescreen.cpp:347</c>) and the allied-visualization switch
/// (<c>igc.h:1127</c>). The panel is shared by both HUD styles (one
/// <c>ServerDirectorySearch.DetailsLines</c> model, one drawing path in
/// <c>Game1.Session</c>), so a single assertion covers Classic and Modern.
/// </summary>
public sealed class P856GameDetailsOptionsTests
{
    private static readonly GameParams Base = new(
        AllowDevelopments: true, Lives: GameParams.UnlimitedLives, ScoresCount: true,
        ObjectModelCreated: true, InProgress: false, GoalConquest: true, GoalDeathmatch: false,
        GoalProsperity: false, GoalCountdown: false, GoalArtifacts: false, GoalFlags: false,
        GoalTerritory: false, MinRank: -1, MaxRank: 1000);

    private static ServerListing Listing(GameParams game) => new(
        Id: "p856", Name: "P856", Address: "127.0.0.1", Port: 6777,
        PlayerCount: 0, MaxPlayers: 16, MissionName: "XXV_simple.igc",
        CoreFile: "PCore001.igc", Version: "0.1",
        RegisteredUtc: DateTimeOffset.UnixEpoch, LastSeenUtc: DateTimeOffset.UnixEpoch,
        Game: game);

    [Fact]
    public void Details_show_both_published_option_switches()
    {
        var on = ServerDirectorySearch.DetailsLines(Listing(Base with
            { AllowFriendlyFire = true, AllowAlliedViz = true }));
        Assert.Contains(on, s => s.StartsWith("FF FIRE:") && s.EndsWith("Yes"));
        Assert.Contains(on, s => s.StartsWith("ALLY VIZ:") && s.EndsWith("Yes"));

        // Native defaults (both off) read as No, exactly like native's YesNo().
        var off = ServerDirectorySearch.DetailsLines(Listing(Base));
        Assert.Contains(off, s => s.StartsWith("FF FIRE:") && s.EndsWith("No"));
        Assert.Contains(off, s => s.StartsWith("ALLY VIZ:") && s.EndsWith("No"));
    }

    [Fact]
    public void Native_yes_no_wording_matches_the_mission_param()
    {
        Assert.Equal("Yes", GameParams.YesNo(true));
        Assert.Equal("No", GameParams.YesNo(false));
    }
}
