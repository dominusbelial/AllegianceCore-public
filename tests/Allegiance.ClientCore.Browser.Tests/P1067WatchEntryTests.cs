using Allegiance.ClientCore.Browser.Pages;

namespace Allegiance.ClientCore.Browser.Tests;

/// <summary>
/// P1067 — the Watch camera's deterministic-seed URL contract: the browser
/// equivalent of the desktop's ALLEGIANCE_DIRECTOR_SEED, honoured only with the
/// developer switch on because a WASM client has no process environment.
/// </summary>
public sealed class P1067WatchEntryTests
{
    [Theory]
    [InlineData("?dev=1&director-seed=1067", true, 1067)]
    [InlineData("director-seed=-42&dev=1", true, -42)]
    [InlineData("?dev=1&online=1&director-seed=7", true, 7)]
    [InlineData("?director-seed=1067", false, null)]              // no developer controls: ignored
    [InlineData("?dev=1", true, null)]
    [InlineData("?dev=1&director-seed=", true, null)]
    [InlineData("?dev=1&director-seed=ten", true, null)]
    [InlineData("?dev=1&director-seed=2147483648", true, null)]   // over int range
    [InlineData("?dev=1&director-seed=123456789012", true, null)]
    [InlineData(null, true, null)]
    public void Director_seed_is_dev_only_and_parsed_defensively(string? query, bool dev, int? expected)
        => Assert.Equal(expected, BrowserEntry.DirectorSeed(query, dev));
}
