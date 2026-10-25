using Allegiance.Update;

namespace Allegiance.Server;

/// <summary>
/// P810 — the server self-update slice (the modernised native
/// <c>AllSrvUpdate</c> role, <c>src/AutoUpdate/AutoUpdate.cpp:325-356</c>).
///
/// The native dedicated server updated through the same CRC-file-list machinery
/// as the client, driven by a standalone tool run between matches. The port
/// stages from a signed release feed instead, and ONLY at game over — the
/// native-like quiet point: <see cref="DeclareGameOver"/> has already put every
/// GAME_OVER packet on the wire and rotated the finished mission, so a feed
/// check on the thread pool cannot touch live state (never mid-match, per the
/// task brief). The staged build is applied by <see cref="UpdateApplier"/> at
/// the next process start (ServerMain applies pending updates before the world
/// loads; Docker/systemd restart policies bring the process back).
/// </summary>
public sealed partial class Server
{
    /// <summary>The last feed-check result, for tests and diagnostics.</summary>
    internal volatile UpdateCheckResult? LastUpdateCheck;

    /// <summary>Schedule the opt-in game-over feed check. No-op unless
    /// <c>updates</c> is fully configured (<see cref="ServerUpdateGate"/>);
    /// fire-and-forget on the thread pool — never blocks or throws into the
    /// sim thread. A failure only logs.</summary>
    internal void ScheduleServerUpdateCheckAtGameOver()
    {
        var updates = _config.Updates;
        if (!ServerUpdateGate.IsConfigured(updates)) return;

        string feedUrl = updates!.FeedUrl;
        string publicKey = updates.PublicKeyPem;
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                var result = await UpdateClient.CheckAndStageAsync(new UpdateClientOptions
                {
                    FeedBaseUrl = feedUrl,
                    PublicKeyPem = publicKey,
                    AppRoot = AppContext.BaseDirectory,
                }, http, Console.Out);
                LastUpdateCheck = result;
                Console.WriteLine(result.Status switch
                {
                    UpdateCheckStatus.Pending => $"[update] staged {result.VersionOrSource} for the next restart " +
                                                 $"({result.ChangedFiles} changed file(s), {result.BytesDownloaded} bytes)",
                    UpdateCheckStatus.UpToDate => $"[update] feed {result.VersionOrSource}: server is up to date",
                    _ => $"[update] check failed: {result.Error}",
                });
            }
            catch (Exception ex)
            {
                // The updater must never take the server down.
                Console.WriteLine($"[update] unexpected failure ignored: {ex.Message}");
            }
        });
    }
}

/// <summary>P810 — the pure gate in front of the server feed check, so the
/// opt-in rules are headless-testable: the updater runs only when the
/// <c>updates</c> section is present, <c>enabled</c>, and names both the feed
/// and the verification key.</summary>
public static class ServerUpdateGate
{
    public static bool IsConfigured(UpdatesConfig? updates)
        => updates is { Enabled: true }
           && !string.IsNullOrWhiteSpace(updates.FeedUrl)
           && !string.IsNullOrWhiteSpace(updates.PublicKeyPem);
}
