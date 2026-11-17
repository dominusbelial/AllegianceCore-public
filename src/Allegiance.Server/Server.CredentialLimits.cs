using System.Net;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

/// <summary>
/// P1022 (R1 finding F7) — the credential-guessing gate the server applies to
/// every place a wrong secret is answered: the game-password join refusal
/// (<see cref="JoinMission"/>), the bearer-secret comparisons at a validated
/// logon (<see cref="AuthenticatePrivileges"/>) and a wrong secret presented
/// before an admin cheat command (<see cref="TryHandleAdminCheat"/>).
/// <para><b>Native:</b> none of this exists natively — a wrong password is
/// answered at once (<c>FedSrv/FedSrv.CPP:648-661</c>) and the bearer GUIDs are
/// compared when they arrive (<c>FedSrv.CPP:664</c>). This is a deliberate port
/// hardening, applying Domingo's R1 decision that EVERY server bounds guessing
/// (not only password-protected ones).</para>
/// <para><b>Loopback is not exempt</b> from the counting, but a correct
/// credential is never slowed, so bots, the harness, local skirmish/training and
/// the rig are unaffected: they only ever record failures when they send a wrong
/// secret, which they do not.</para>
/// </summary>
public sealed partial class Server
{
    private CredentialGuessLimitsConfig _credentialLimits = new();
    private CredentialGuessGuard _credentialGuesses = new(new CredentialGuessLimitsConfig());
    private readonly PerPeerRateLimiter _lockoutLog = new() { WindowMs = 60_000, MaxPerWindow = 4, SummaryEvery = 25 };

    /// <summary>Test seam: the source address a synthetic peer is treated as
    /// having (the production path asks the transport, which has no entry for a
    /// peer the test injected with <see cref="TestProcessDatagram"/>).</summary>
    internal Func<ConnectionId, IPAddress?>? TestPeerAddressOverride { get; set; }

    /// <summary>The guard itself (tests drive it directly for the pure
    /// window/doubling/expiry rules).</summary>
    internal CredentialGuessGuard CredentialGuesses => _credentialGuesses;

    /// <summary>
    /// P1022 — resolve, validate and banner <c>credentialGuessLimits</c> once at
    /// boot, so every run is labelled with the active guessing limits (a
    /// rate-limit experiment must never be mistaken for the native path).
    /// Default ON.
    /// </summary>
    private void ResolveCredentialGuessLimits()
    {
        var limits = _config.CredentialGuessLimits ?? new CredentialGuessLimitsConfig();
        if (!limits.Enabled)
        {
            // An explicitly disabled section must not validate — the operator
            // asked for the unthrottled native path.
            _credentialLimits = limits;
            _credentialGuesses = new CredentialGuessGuard(limits);
            Console.WriteLine("[boot] credential guess limits OFF (credentialGuessLimits.enabled=false) — "
                + "native behaviour: every wrong password/secret is answered at once (FedSrv.CPP:648-661)");
            return;
        }
        limits.Validate();
        _credentialLimits = limits;
        _credentialGuesses = new CredentialGuessGuard(limits);
        Console.WriteLine($"[boot] credential guess limits ON: {limits.MaxFailuresPerConnection} failure(s) close the connection, "
            + $"{limits.FailuresPerWindow} per address in {limits.WindowSeconds:0.##}s -> "
            + $"{limits.BaseLockoutSeconds:0.##}s lockout doubling to {limits.MaxLockoutSeconds:0.##}s "
            + $"(table cap {limits.MaxTrackedAddresses}; port hardening, native has no throttle — R1 F7)");
    }

    /// <summary>
    /// The per-source-address key a peer is counted under. The transport's own
    /// accept-time address (never greeting/name data) is used when available; a
    /// peer the transport cannot resolve (a test-injected connection) falls back
    /// to its connection id, which is the conservative choice — an unknown
    /// address must never make unrelated peers share one lockout bucket.
    /// </summary>
    private string PeerAddressKey(ConnectionId peer)
    {
        if (TestPeerAddressOverride?.Invoke(peer) is { } injected) return Canonical(injected);
        return ResolvePeerAddress(peer) is { } address ? Canonical(address) : $"peer:{peer.Value}";
    }

    private IPAddress? ResolvePeerAddress(ConnectionId peer) => _host.Transport?.PeerAddress(peer);

    private static string Canonical(IPAddress address)
        => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    /// <summary>True when this connection (or its source address) is inside a
    /// credential back-off: the caller drops the frame without evaluating the
    /// presented password/secret at all.</summary>
    internal bool CredentialGuessBlocked(ConnectionId peer)
        => _credentialGuesses.IsLockedOut(peer, PeerAddressKey(peer), SimNow);

    /// <summary>
    /// P1022 — record one FAILED credential comparison and apply the two limits:
    /// the connection is retired once its own failures reach the configured
    /// maximum, and the source address gets a doubling back-off. Returns true
    /// when the connection was retired (the caller need not do anything else).
    /// </summary>
    private bool RecordCredentialGuessFailure(ConnectionId peer, string route)
    {
        string address = PeerAddressKey(peer);
        var outcome = _credentialGuesses.RecordFailure(peer, address, SimNow);
        var ticket = _lockoutLog.Ticket(RefusalLogKey(peer, address));

        if (outcome.CloseConnection)
        {
            if (ticket.Emit)
                Console.WriteLine($"[lockout] {peer} ({route}): {_credentialLimits.MaxFailuresPerConnection} failed credentials on this connection — closing it "
                    + "(one credential evaluation per connection; native has no throttle, R1 F7)");
            // Same retirement path LOGOFF/H-14 uses: the refusal already queued its
            // reply, so close only once it has reached the transport.
            var transport = _host.Transport;
            if (transport is not null) AfterQueuedSends(() => transport.ClosePeerAfterFlush(peer));
        }

        if (outcome.LockedOut && ticket.Emit)
        {
            Console.WriteLine($"[lockout] address {address}: {outcome.AddressFailures} failed credentials in {_credentialLimits.WindowSeconds:0.##}s — "
                + $"credential evaluation refused for {outcome.LockoutSeconds:0.##}s (doubling to {_credentialLimits.MaxLockoutSeconds:0.##}s; native has no throttle, R1 F7)");
        }
        else if (!ticket.Emit && ticket.Summarize)
        {
            Console.WriteLine($"[lockout] {ticket.Suppressed} more credential refusals/lockouts suppressed (rate-limited)");
        }
        return outcome.CloseConnection;
    }

    /// <summary>P1022 — a CORRECT credential on this connection: clear its
    /// failure count so a player who mistyped once is never retired afterwards
    /// (the address window is not reset — see <see cref="CredentialGuessGuard.RecordSuccess"/>).</summary>
    private void RecordCredentialGuessSuccess(ConnectionId peer)
    {
        if (!_credentialLimits.Enabled) return;
        _credentialGuesses.RecordSuccess(peer);
    }

    /// <summary>Test-facing: the address a peer is currently counted under.</summary>
    internal string AddressKeyOf(ConnectionId peer) => PeerAddressKey(peer);
}
