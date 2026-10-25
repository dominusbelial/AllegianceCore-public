using System.Net.Http;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P810 — the client auto-update bootstrap, run from <c>Program.Main</c>
/// before SDL/GL/audio load:
///
/// <list type="number">
/// <item><b>Apply-on-restart</b>: copy any staged pending update over the
/// application tree (fail-safe; the old build boots on any error). Native's
/// AutoDownload did the same move-then-restart dance
/// (<c>clintlib/AutoDownload.cpp:225-240</c>).</item>
/// <item><b>The update check</b> (native's AUTO_UPDATE_INFO trigger,
/// <c>clintlib/appmsg.cpp:3398-3430</c>): ask the remembered lobby's
/// <c>GET /api/update/info</c>; when it advertises a feed AND a version newer
/// than installed (current or the hard minimum), check the signed feed and
/// stage the newer build for the next start. Native disconnected the lobby
/// during a download (<c>appmsg.cpp:3419</c>); the port checks before
/// connecting, so there is nothing to disconnect (design, modernised).</item>
/// </list>
///
/// The feed can be forced with <c>ALLEGIANCE_UPDATE_FEED</c> (env) or
/// <c>--update-feed &lt;url&gt;</c> (args). The ECDSA P-256 verification key
/// comes from <c>ALLEGIANCE_UPDATE_PUBLIC_KEY</c> (PEM), a
/// <c>ALLEGIANCE_UPDATE_PUBLIC_KEY_FILE</c> path, or
/// <c>&lt;appRoot&gt;/autoupdate/feed-public.pem</c>. Without a verifiable key
/// nothing is staged — the updater never installs unverified bytes.
///
/// This class is deliberately thin (env/arg plumbing only): every rule it
/// relies on — signature verification, the delta computation, the staging
/// layout, the version gate — lives in <see cref="Allegiance.Update"/> and is
/// covered there headlessly (tests/Allegiance.Update.Tests).
/// </summary>
internal static class ClientUpdateBootstrap
{
    /// <summary>Keep boot snappy: a dead/slow lobby must not hold the intro
    /// hostage (a failed check just skips staging).</summary>
    private static readonly TimeSpan LobbyTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(60);

    public static async Task RunAsync(string[] args)
    {
        try
        {
            string appRoot = AppContext.BaseDirectory;

            // 1. Apply-on-restart, before anything else loads.
            Allegiance.Update.UpdateApplier.ApplyPendingIfAny(appRoot, Console.Out);

            // 2. Resolve the feed source (env/arg override, else the lobby's
            //    advertisement) and the verification key.
            string? feed = Environment.GetEnvironmentVariable("ALLEGIANCE_UPDATE_FEED") ?? ArgValue(args, "--update-feed");
            string? publicKey = ResolvePublicKey(appRoot);
            if (publicKey is null)
            {
                if (feed is not null)
                    Console.WriteLine("[update] no verification key configured (ALLEGIANCE_UPDATE_PUBLIC_KEY / feed-public.pem); skipping");
                return;
            }

            using var http = new HttpClient();
            if (feed is null)
            {
                string? lobbyUrl = ResolveLobbyUrl(args);
                if (lobbyUrl is null) return;

                http.Timeout = LobbyTimeout;
                var info = await Allegiance.Update.LobbyUpdateInfoClient.FetchAsync(lobbyUrl, http);
                if (string.IsNullOrWhiteSpace(info?.FeedUrl))
                    return; // the lobby advertises nothing — the unconfigured-native-lobby case

                string installed = Allegiance.Update.UpdateState.ResolveInstalledVersion(appRoot);
                bool currentIsNewer = Allegiance.Update.UpdateVersionGate.IsNewer(info!.CurrentVersion, installed);
                bool belowMinimum = Allegiance.Update.UpdateVersionGate.IsTooOld(installed, info.MinimumVersion);
                if (!currentIsNewer && !belowMinimum) return;

                feed = info.FeedUrl!;
                Console.WriteLine(belowMinimum
                    ? $"[update] lobby requires at least {info.MinimumVersion} (installed {installed})"
                    : $"[update] lobby advertises a newer client ({info.CurrentVersion}, installed {installed})");
            }

            http.Timeout = FeedTimeout;
            await Allegiance.Update.UpdateClient.CheckAndStageAsync(new Allegiance.Update.UpdateClientOptions
            {
                FeedBaseUrl = feed,
                PublicKeyPem = publicKey,
                AppRoot = appRoot,
            }, http, Console.Out);
        }
        catch (Exception ex)
        {
            // The updater must never block or crash the boot path.
            Console.WriteLine($"[update] bootstrap failed, continuing: {ex.Message}");
        }
    }

    /// <summary>PEM body of the ECDSA P-256 feed verification key: the env var,
    /// a file the env var names, or a file shipped beside the application.</summary>
    internal static string? ResolvePublicKey(string appRoot)
    {
        var inline = Environment.GetEnvironmentVariable("ALLEGIANCE_UPDATE_PUBLIC_KEY");
        if (!string.IsNullOrWhiteSpace(inline))
            return inline.Contains("BEGIN", StringComparison.Ordinal) ? inline
                : inline.Replace("\\n", "\n");

        var path = Environment.GetEnvironmentVariable("ALLEGIANCE_UPDATE_PUBLIC_KEY_FILE");
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return File.ReadAllText(path);
            var bundled = Path.Combine(appRoot, Allegiance.Update.UpdateFeed.StateDir, "feed-public.pem");
            if (File.Exists(bundled))
                return File.ReadAllText(bundled);
        }
        catch (IOException) { /* unreadable key file = no key */ }
        return null;
    }

    /// <summary>The lobby the intro would have polled (same precedence as
    /// Main's own parsing: --lobby wins, then the remembered player config).</summary>
    internal static string? ResolveLobbyUrl(string[] args)
    {
        var fromArgs = ArgValue(args, "--lobby");
        if (!string.IsNullOrWhiteSpace(fromArgs)) return fromArgs;
        try
        {
            var player = PlayerConfig.Load();
            return string.IsNullOrWhiteSpace(player.Lobby) ? null : player.Lobby;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(name + '=', StringComparison.Ordinal))
                return args[i][(name.Length + 1)..];
            if (args[i] == name && i + 1 < args.Length)
                return args[i + 1];
        }
        return null;
    }
}
