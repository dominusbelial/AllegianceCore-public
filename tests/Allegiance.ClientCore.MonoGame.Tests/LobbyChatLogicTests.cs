using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the server-browser row formatting (<see cref="ServerListing.FormatRow"/>)
/// — the exact non-trivial string logic the browser UI leans on, kept out of the
/// renderer so it is unit-testable without a graphics device (the same discipline
/// as <see cref="HudFormat"/> / <see cref="ChatLogFormat"/>).
/// </summary>
public class ServerListingTests
{
    private static ServerListing Listing(
        string name = "Test Server", string addr = "127.0.0.1", int port = 6777,
        int players = 2, int max = 8, string mission = "XXV_simple.igc") => new(
        Id: "id1", Name: name, Address: addr, Port: port, PlayerCount: players,
        MaxPlayers: max, MissionName: mission, CoreFile: "PCore001.igc",
        Version: "0.1", RegisteredUtc: DateTimeOffset.UtcNow, LastSeenUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void FormatRow_Includes_Name_AddressPort_Players_Mission()
    {
        var line = Listing().FormatRow();
        Assert.Contains("Test Server", line);
        Assert.Contains("127.0.0.1:6777", line);
        Assert.Contains("2/8", line);
        Assert.Contains("XXV_simple.igc", line);
    }

    [Fact]
    public void FormatRow_EmptyName_FallsBackToPlaceholder()
    {
        Assert.Contains("(unnamed)", Listing(name: "").FormatRow());
        Assert.Contains("(unnamed)", Listing(name: "   ").FormatRow());
    }

    [Fact]
    public void FormatRow_ZeroOrNegativeMaxPlayers_ShowsCountOnly()
    {
        // MaxPlayers 0 signals "unknown/unbounded" in the register contract — the
        // browser must not print a bogus "0/0".
        Assert.DoesNotContain("/", Listing(max: 0).FormatRow());
        Assert.Contains("2 players", Listing(max: 0).FormatRow());
    }

    [Fact]
    public void FormatRow_InvalidAddressOrPort_FallsBackGracefully()
    {
        // Empty address + zero port → "?:?" placeholder, no crash.
        Assert.Contains("?:?", Listing(addr: "", port: 0).FormatRow());
    }

    [Fact]
    public void FormatRow_NoPlayersNoMax_StillFormatsWithoutSlash()
    {
        Assert.Equal("[unverified] (unnamed)  ?:?   0 players   -",
            Listing(name: "", addr: "", port: 0, players: 0, max: 0, mission: "").FormatRow());
    }
}

/// <summary>
/// Pure-logic tests for the chat-line formatter (<see cref="ChatLogFormat.Format"/>):
/// the sender-name/message composition and the channel-tag mapping.
/// </summary>
public class ChatLogFormatTests
{
    [Fact]
    public void Format_Everyone_HasNoTag()
    {
        Assert.Equal("Alice: hello", ChatLogFormat.Format("Alice", "hello", ChatTarget.Everyone));
    }

    [Fact]
    public void Format_UnknownSender_OmitsPrefix()
    {
        Assert.Equal("standalone message", ChatLogFormat.Format("", "standalone message", ChatTarget.Everyone));
        Assert.Equal("standalone message", ChatLogFormat.Format(null!, "standalone message", ChatTarget.Everyone));
    }

    [Theory]
    [InlineData(ChatTarget.Team, "[TEAM]")]
    [InlineData(ChatTarget.Allies, "[ALLIES]")]
    [InlineData(ChatTarget.Leaders, "[LEADERS]")]
    [InlineData(ChatTarget.Group, "[GROUP]")]
    [InlineData(ChatTarget.AllSector, "[SECTOR]")]
    [InlineData(ChatTarget.FriendlySector, "[FSECTOR]")]
    [InlineData(ChatTarget.Admin, "[ADMIN]")]
    public void Format_NonEveryoneChannels_GetBracketTags(ChatTarget target, string tag)
    {
        Assert.StartsWith(tag, ChatLogFormat.Format("Bob", "hi", target));
        Assert.Contains("Bob: hi", ChatLogFormat.Format("Bob", "hi", target));
    }

    [Fact]
    public void Format_WeirdTarget_DoesNotCrashAndHasNoTag()
    {
        Assert.Equal("Carol: hey", ChatLogFormat.Format("Carol", "hey", (ChatTarget)999));
    }

    [Fact]
    public void Format_NonAsciiCharacters_AreMappedToQuestionMarks()
    {
        // The HUD spritefonts bake ASCII 32..126 only; anything higher must be
        // scrubbed to '?' so DrawString never throws on a foreign chat line.
        Assert.Equal("Jos?: hola", ChatLogFormat.Format("Jos\u00e9", "hola", ChatTarget.Everyone));
        Assert.Equal("anon: caf?", ChatLogFormat.Format("anon", "caf\u00e9", ChatTarget.Everyone));
    }

    [Fact]
    public void AsciiSafe_KeepsPlainAsciiUntouched()
    {
        const string plain = "ABC abc 123 !@#$%^&*()_+-={}[]|;:,.<>?/~`";
        Assert.Equal(plain, ChatLogFormat.AsciiSafe(plain));
    }
}
