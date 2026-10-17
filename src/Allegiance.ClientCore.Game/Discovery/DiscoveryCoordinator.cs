using System.Diagnostics;

namespace Allegiance.ClientCore.MonoGame.Discovery;

/// <summary>Tunables for a <see cref="DiscoveryCoordinator"/> scan. All defaults
/// are sane for the intro screen; tests override the injectable seams.</summary>
public sealed class DiscoveryScanOptions
{
    public bool EnableLan = true;
    public bool EnableTailscale = true;
    public bool EnableSubnet = true;
    public bool EnableFavorites = true;
    /// <summary>Bounded probe concurrency for the subnet/Tailscale sweeps.</summary>
    public int MaxParallelProbes = 16;
    public int DiscoveryPort = DiscoveryDefaults.Port;
    /// <summary>Remembered server identities (host or host:port) to refresh.</summary>
    public IReadOnlyList<string> Favorites = Array.Empty<string>();
    /// <summary>Local interfaces for LAN + subnet enumeration. Null = enumerate
    /// the real machine's interfaces (production); tests inject synthetic ones.</summary>
    public IReadOnlyList<InterfaceInfo>? Interfaces;
    /// <summary>Tailscale status runner; null = the real <c>tailscale</c> CLI.
    /// Tests inject a canned JSON producer so no external process is needed.</summary>
    public Func<string?>? TailscaleStatus;
    /// <summary>Optional function the coordinator calls per LAN-collected or
    /// probe result (observability / tests).</summary>
    public Action<DiscoveredServer>? OnFound;
}

/// <summary>
/// P731 — merges every "Find Server" discovery source into one de-duplicated
/// list. Sources (Domingo, 2026-10-16):
/// <list type="number">
///   <item>LAN — UDP broadcast + multicast to the responder (<see cref="DiscoveryTargets.LanEndpoints"/>);</item>
///   <item>Tailscale — <c>tailscale status --json</c> online peers, each
///        unicast-probed on its Tailscale IP, showing its MagicDNS name. Absent
///        Tailscale is silent;</item>
///   <item>Subnet — a bounded unicast sweep of each active non-loopback
///        interface's subnet (&lt;= /24, capped), so VPN interfaces (WireGuard,
///        OpenVPN, ZeroTier) are covered;</item>
///   <item>Favorites — remembered addresses from <c>player.json</c>, refreshed
///        by unicast query;</item>
///   <item>Lobby — merged by the caller (the online <c>/api/servers</c> list),
///        so a lobby reachable over the VPN also appears here.</item>
/// </list>
/// Entries are de-duplicated by <c>host:port</c> (first-wins by source order:
/// Favorites, LAN, Tailscale, Subnet), and each carries its source + ping.
/// </summary>
public sealed class DiscoveryCoordinator
{
    private readonly DiscoveryEngine _engine;

    public DiscoveryCoordinator(DiscoveryEngine engine)
    {
        _engine = engine;
    }

    /// <summary>Run a scan and return the merged, de-duplicated list.</summary>
    public List<DiscoveredServer> Scan(DiscoveryScanOptions o, CancellationToken ct = default)
    {
        var merged = new Dictionary<string, DiscoveredServer>();
        void Add(DiscoveredServer s)
        {
            if (merged.ContainsKey(s.HostPort)) return;   // first-wins by source order
            merged[s.HostPort] = s;
            o.OnFound?.Invoke(s);
        }

        // Favorites first (known-good remembered servers survive dedupe).
        if (o.EnableFavorites && o.Favorites.Count > 0)
        {
            foreach (var fav in DiscoveryTargets.FavoriteEndpoints(o.Favorites, o.DiscoveryPort))
            {
                ct.ThrowIfCancellationRequested();
                var s = _engine.ProbeTargetAsync(fav, ct);
                if (s is not null) Add(s);
            }
        }

        // LAN broadcast + multicast (native's poll).
        if (o.EnableLan)
        {
            var ifaces = o.Interfaces ?? DiscoveryTargets.GetLocalInterfaces();
            var lanTargets = DiscoveryTargets.LanEndpoints(ifaces, o.DiscoveryPort, DiscoveryDefaults.MulticastGroup);
            foreach (var s in _engine.CollectLanAsync(lanTargets, ct: ct))
                Add(s);
        }

        // Tailscale (silent when absent).
        if (o.EnableTailscale)
        {
            string? json;
            try { json = o.TailscaleStatus?.Invoke() ?? RunTailscaleStatus(); }
            catch { json = null; }                       // absent/error → silent
            if (!string.IsNullOrWhiteSpace(json))
            {
                var peers = DiscoveryTargets.ParseTailscalePeers(json);
                foreach (var s in ProbeMany(
                    peers.Select(p => new DiscoveryTarget(p.Address.ToString(), o.DiscoveryPort, DiscoverySource.Tailscale, p.DisplayName)),
                    o.MaxParallelProbes, ct))
                {
                    if (s is not null) Add(s);
                }
            }
        }

        // Subnet sweep (bounded, capped, cancellable).
        if (o.EnableSubnet)
        {
            var ifaces = o.Interfaces ?? DiscoveryTargets.GetLocalInterfaces();
            var sub = DiscoveryTargets.SubnetHosts(ifaces, o.DiscoveryPort);
            foreach (var s in ProbeMany(sub, o.MaxParallelProbes, ct))
            {
                if (s is not null) Add(s);
            }
        }

        return merged.Values.ToList();
    }

    private List<DiscoveredServer?> ProbeMany(
        IEnumerable<DiscoveryTarget> targets, int maxParallel, CancellationToken ct)
    {
        var results = new List<DiscoveredServer?>();
        using var sem = new SemaphoreSlim(Math.Max(1, maxParallel));
        var work = new List<Task<DiscoveredServer?>>();
        foreach (var t in targets)
        {
            ct.ThrowIfCancellationRequested();
            sem.Wait(ct);
            work.Add(Task.Run(() =>
            {
                try { return _engine.ProbeTargetAsync(t, ct); }
                finally { sem.Release(); }
            }, ct));
        }
        Task.WaitAll(work.ToArray());
        foreach (var w in work) results.Add(w.Result);
        return results;
    }

    /// <summary>Run <c>tailscale status --json</c> and return stdout, or null when
    /// the CLI is absent / errors (silent, per the task brief).</summary>
    public static string? RunTailscaleStatus()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "tailscale",
                Arguments = "status --json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return p.HasExited && p.ExitCode == 0 ? outp : null;
        }
        catch (Exception)
        {
            return null;   // tailscale not installed / not permitted → silent
        }
    }
}
