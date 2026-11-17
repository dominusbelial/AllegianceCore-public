using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    private readonly HashSet<ConnectionId> _adminAuthenticated = [];
    private readonly HashSet<ConnectionId> _harnessAuthenticated = [];

    private bool PrivilegedConnection(ConnectionId from, string? secret, HashSet<ConnectionId> authenticated)
        => string.IsNullOrEmpty(secret)
            ? _host.Transport is { } transport && transport.IsPeerLoopback(from)
            : authenticated.Contains(from);

    private bool IsHarnessPeer(ConnectionId from, string? name)
    {
        var th = _config.LiveSettings?.TestHarness;
        return th is { Enabled: true } && !string.IsNullOrEmpty(th.CharacterNamePrefix)
            && name is not null && name.StartsWith(th.CharacterNamePrefix, StringComparison.Ordinal)
            && PrivilegedConnection(from, th.Secret, _harnessAuthenticated);
    }

    /// <summary>
    /// P655 — the bearer-secret grants, compared fixed-time against
    /// <c>LOGONREQ.CDKey</c>.
    /// <para><b>P1022 (R1 F7)</b> — a WRONG bearer counts toward the credential
    /// guessing limits, so an admin/bot/harness secret cannot be guessed at the
    /// speed of reconnects either. It is counted only for a peer that CLAIMS the
    /// role it is guessing: an allow-listed admin name for the admin secret, or a
    /// harness-prefixed name for the harness secret. That discriminator is
    /// required, not a convenience: every ordinary client of this port presents
    /// a fixed placeholder CD key (<c>src/Allegiance.ClientCore.Game/Game1.Session.cs</c>
    /// "ABCDEFGHIJKL", <c>src/Allegiance.Bot/Program.cs</c> "AAAABBBBCCCC"), so
    /// counting every non-matching CD key would throttle every player and every
    /// bot sharing an address — the opposite of the finding's "correct
    /// credentials are never slowed". An empty CD key is no attempt at all, and
    /// the bot-role secret has no name/prefix claim on the wire, so a wrong bot
    /// CD key is not counted either (stated in the report).
    /// </para>
    /// </summary>
    private void AuthenticatePrivileges(ConnectionId from, LogonRequest req)
    {
        string presented = req.CDKey ?? "";
        var settings = _config.LiveSettings;
        string? bot = _config.BotAuthenticationSecret;
        var adminCfg = settings?.AdminCheats;
        if (adminCfg is not { Enabled: true, Secret: { Length: > 0 } }) adminCfg = null;
        var harness = settings?.TestHarness;
        if (harness is not { Enabled: true, Secret: { Length: > 0 } }) harness = null;
        string? admin = adminCfg?.Secret;
        string? harnessSecretValue = harness?.Secret;

        bool matchedBot = bot is { Length: > 0 } && FixedTimeEquals(bot, presented);
        bool matchedAdmin = admin is { Length: > 0 } && FixedTimeEquals(admin, presented);
        bool matchedHarness = harnessSecretValue is { Length: > 0 } && FixedTimeEquals(harnessSecretValue, presented);
        bool matchedModerator = IsModeratorSecret(presented);

        if (matchedBot) _botAuthenticated.Add(from);
        if (matchedAdmin) _adminAuthenticated.Add(from);
        if (matchedHarness) _harnessAuthenticated.Add(from);

        bool anyCredential = matchedBot || matchedAdmin || matchedHarness || matchedModerator;
        if (anyCredential)
        {
            // A correct credential is never slowed (R1 F7).
            RecordCredentialGuessSuccess(from);
            return;
        }
        if (presented.Length == 0) return;   // no credential presented — not an attempt

        // A non-matching bearer is a guess only when the peer claims that role.
        if (adminCfg is not null && AdminSpectatorAllowed(adminCfg, req.CharacterName))
        {
            RecordCredentialGuessFailure(from, "admin-secret");
        }
        else if (harness?.CharacterNamePrefix is { Length: > 0 } prefix
                 && req.CharacterName is { } name && name.StartsWith(prefix, StringComparison.Ordinal))
        {
            RecordCredentialGuessFailure(from, "harness-secret");
        }
    }

    /// <summary>A configured moderator bearer (P762) — a correct credential, so
    /// it never counts as a wrong admin/harness secret.</summary>
    private bool IsModeratorSecret(string presented)
    {
        if (presented.Length == 0 || _config.ModeratorAccounts.Length == 0) return false;
        foreach (var account in _config.ModeratorAccounts)
        {
            if (string.IsNullOrWhiteSpace(account)) continue;
            if (FixedTimeEquals(account, presented)) return true;
        }
        return false;
    }
}
