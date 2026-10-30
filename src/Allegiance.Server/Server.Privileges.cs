using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    private readonly HashSet<ConnectionId> _adminAuthenticated = [];
    private readonly HashSet<ConnectionId> _harnessAuthenticated = [];

    private bool PrivilegedConnection(ConnectionId from, string? secret, HashSet<ConnectionId> authenticated)
        => string.IsNullOrEmpty(secret)
            ? _host.Transport is { } tcp && tcp.IsPeerLoopback(from)
            : authenticated.Contains(from);

    private bool IsHarnessPeer(ConnectionId from, string? name)
    {
        var th = _config.LiveSettings?.TestHarness;
        return th is { Enabled: true } && !string.IsNullOrEmpty(th.CharacterNamePrefix)
            && name is not null && name.StartsWith(th.CharacterNamePrefix, StringComparison.Ordinal)
            && PrivilegedConnection(from, th.Secret, _harnessAuthenticated);
    }

    private void AuthenticatePrivileges(ConnectionId from, LogonRequest req)
    {
        if (_config.BotAuthenticationSecret is { Length: > 0 } bot
            && FixedTimeEquals(bot, req.CDKey ?? "")) _botAuthenticated.Add(from);
        var settings = _config.LiveSettings;
        if (settings?.AdminCheats is { Enabled: true, Secret: { Length: > 0 } admin }
            && FixedTimeEquals(admin, req.CDKey ?? "")) _adminAuthenticated.Add(from);
        if (settings?.TestHarness is { Enabled: true, Secret: { Length: > 0 } harness }
            && FixedTimeEquals(harness, req.CDKey ?? "")) _harnessAuthenticated.Add(from);
    }
}
