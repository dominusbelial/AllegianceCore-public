namespace Allegiance.ClientCore.MonoGame.Discovery;

/// <summary>Where a discovered server came from. The intro screen's "Find
/// Server" list merges several discovery sources and shows each row's source
/// (native's LAN enumeration was a single source; the port deliberately widens
/// it so servers over VPNs and Tailscale are findable too — Domingo, 2026-10-16).</summary>
public enum DiscoverySource
{
    /// <summary>Found on the local subnet via UDP broadcast (native's
    /// <c>FindStandaloneServersByName</c> LAN enumeration, introscreen.cpp:905).</summary>
    Lan,
    /// <summary>Found via IPv4 multicast (complement to broadcast; some
    /// networks/NICs only carry one).</summary>
    Multicast,
    /// <summary>Found by unicasting a Tailscale peer's Tailscale IP (from
    /// <c>tailscale status --json</c>).</summary>
    Tailscale,
    /// <summary>Found by a bounded unicast sweep of a local interface's subnet
    /// (catches VPN interfaces such as WireGuard/OpenVPN/ZeroTier).</summary>
    Subnet,
    /// <summary>A remembered favorite / recently-used address from
    /// <c>player.json</c>, refreshed by a unicast query.</summary>
    Favorite,
    /// <summary>Came from the online lobby directory (<c>/api/servers</c>), not
    /// a UDP probe — merged into the same list so a lobby reachable over the
    /// VPN also works.</summary>
    Lobby,
    /// <summary>A manually typed <c>host[:port]</c> direct-connect entry (a
    /// modern addition — native had no direct-IP field, see introscreen.cpp
    /// OnTryLogon's ALLEGIANCE_DIRECT_SERVER comment at :1630-1656).</summary>
    Manual,
}

/// <summary>
/// P731 — one server in the merged "Find Server" list. Pure value record
/// (unit-testable without a graphics device). <see cref="Address"/> is the
/// host to connect to (the probing IP, or the MagicDNS name for Tailscale),
/// <see cref="Port"/> is the <b>game</b> port (from the discovery reply's
/// <c>gamePort</c>), distinct from the discovery responder port used to probe.
/// </summary>
public sealed record DiscoveredServer(
    string Name,
    string Address,
    int Port,
    DiscoverySource Source,
    int PingMs,
    string Map,
    int Players,
    int MaxPlayers,
    string Version,
    string DisplayName)
{
    public string SourceLabel => Source switch
    {
        DiscoverySource.Lan => "LAN",
        DiscoverySource.Multicast => "Multicast",
        DiscoverySource.Tailscale => "Tailscale",
        DiscoverySource.Subnet => "Subnet",
        DiscoverySource.Favorite => "Favorite",
        DiscoverySource.Lobby => "Lobby",
        DiscoverySource.Manual => "Manual",
        _ => "?",
    };

    /// <summary>Identity used for de-duplication across sources: the connect
    /// target. Two rows from different sources with the same host:port collapse
    /// to one (the stronger/most recent source wins the display).</summary>
    public string HostPort => $"{Address}:{Port}";

    /// <summary>Player-count text, e.g. "3/100" or "3" when max is unknown.</summary>
    public string PlayersText => MaxPlayers > 0
        ? $"{Players}/{MaxPlayers}"
        : Players.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One list row: name, address:port, players, source, ping.
    /// Pure string logic so it is unit-testable.</summary>
    public string FormatRow()
    {
        var name = string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name;
        var addr = string.IsNullOrWhiteSpace(Address) ? "?" : Address;
        var display = string.IsNullOrWhiteSpace(DisplayName) ? "" : $"  [{DisplayName}]";
        string ping = PingMs > 0 ? $"{PingMs}ms" : "-";
        string map = string.IsNullOrWhiteSpace(Map) ? "" : $"  {Map}";
        return $"{name}  {addr}:{Port}  {PlayersText} players  [{SourceLabel}] {ping}{display}{map}";
    }
}
