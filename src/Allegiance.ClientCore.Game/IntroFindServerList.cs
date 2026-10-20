namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P757 — the pure rules of the intro screen's Find Server (LAN) list, native
/// <c>IntroScreen::FindServerPopup::FindGames</c> (introscreen.cpp:824-995):
/// <list type="bullet">
/// <item>the DPlay8 EnumHosts results are de-duplicated (the 2005 WLP note,
/// introscreen.cpp:903-931);</item>
/// <item>single-player games and Zone Club servers are hidden — only sessions
/// with <c>nMaxPlayers &gt; 1 &amp;&amp; nMaxPlayers &lt; 1000</c> are listed
/// (introscreen.cpp:979-983). The port's lobby listings carry
/// <c>MaxPlayers == 0</c> for "no advertised cap", so 0 passes as
/// unknown/unbounded (the port's documented directory convention);</item>
/// <item>the list is sorted by game name (the native
/// <c>SortedList(GameNameCompare)</c>, introscreen.cpp:841);</item>
/// <item>an empty result set shows the "Searching..." placeholder
/// (introscreen.cpp:848-851 — the port's view already does).</item>
/// </list>
/// Pure so the rules are headless-testable without a discovery engine.
/// </summary>
public static class IntroFindServerList
{
    /// <summary>Native single-player/Zone cap bounds (introscreen.cpp:980-983).</summary>
    public const int MinListedMaxPlayers = 2;     // nMaxPlayers > 1
    public const int MaxListedMaxPlayers = 999;   // nMaxPlayers < 1000

    /// <summary>Does one discovered server survive the native max-player rule?
    /// <c>maxPlayers == 0</c> is the port's "unknown/unbounded" and passes.</summary>
    public static bool PassesMaxPlayerRule(int maxPlayers)
        => maxPlayers == 0 || (maxPlayers > MinListedMaxPlayers - 1 && maxPlayers < MaxListedMaxPlayers + 1);

    /// <summary>Apply the whole FindGames post-processing: de-duplicate by
    /// host:port, drop sessions failing the max-player rule, and sort by game
    /// name (case-insensitive, ties by host:port — the native GameNameCompare
    /// over the SortedList).</summary>
    public static List<Discovery.DiscoveredServer> FilterAndSort(IEnumerable<Discovery.DiscoveredServer> servers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<Discovery.DiscoveredServer>();
        foreach (var s in servers)
        {
            if (!PassesMaxPlayerRule(s.MaxPlayers)) continue;
            if (!seen.Add($"{s.Address}:{s.Port}")) continue;   // DPlay8 EnumHosts duplicates
            list.Add(s);
        }
        list.Sort((a, b) =>
        {
            int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare($"{a.Address}:{a.Port}", $"{b.Address}:{b.Port}", StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }
}
