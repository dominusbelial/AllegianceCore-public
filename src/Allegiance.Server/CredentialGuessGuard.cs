using Allegiance.Core.Transport;

namespace Allegiance.Server;

/// <summary>
/// P1022 (R1 finding F7) — the credential-guessing limits, configured from the
/// <c>credentialGuessLimits</c> section of <c>server.json</c>. <b>Default ON</b>
/// so every server (password-protected or not, and the bearer-secret roles)
/// bounds guessing out of the box.
/// <para><b>Native:</b> native has no throttle at all — a wrong game password is
/// answered immediately at the join (<c>FedSrv/FedSrv.CPP:648-661</c>) and the
/// bearer GUIDs are compared on arrival (<c>FedSrv/FedSrv.CPP:664</c>,
/// <c>:5817</c>), so this whole section is a deliberate port hardening, never a
/// parity claim. See <c>docs/reports/R1_DISPATCH_LIMITS_P1022.md</c>.</para>
/// </summary>
public sealed class CredentialGuessLimitsConfig
{
    /// <summary>Master switch (default <c>true</c>). <c>false</c> restores the
    /// unthrottled native behaviour for every credential path — the config-off
    /// control arm of the F7 tests.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Bad credentials on ONE connection before that connection is
    /// retired (default 3). One LOGONREQ per connection means a game-password
    /// guesser needs a new connection per guess; the chat-secret path can fail
    /// repeatedly on one, which this closes.</summary>
    public int MaxFailuresPerConnection { get; set; } = 3;

    /// <summary>Failures from one SOURCE ADDRESS, inside
    /// <see cref="WindowSeconds"/>, that trigger a back-off (default 5).</summary>
    public int FailuresPerWindow { get; set; } = 5;

    /// <summary>The failure-counting window in seconds (default 60).</summary>
    public double WindowSeconds { get; set; } = 60;

    /// <summary>The first back-off in seconds (default 15). Each later back-off
    /// for the same address doubles it, up to <see cref="MaxLockoutSeconds"/>.</summary>
    public double BaseLockoutSeconds { get; set; } = 15;

    /// <summary>The back-off ceiling in seconds (default 300 — "a few
    /// minutes").</summary>
    public double MaxLockoutSeconds { get; set; } = 300;

    /// <summary>Hard cap on the per-address table (default 4096 entries). The
    /// table is also pruned of expired entries, so a long uptime or a spoofed
    /// source address cannot grow it without bound.</summary>
    public int MaxTrackedAddresses { get; set; } = 4096;

    public void Validate()
    {
        if (MaxFailuresPerConnection is < 1 or > 1000)
            throw new System.IO.InvalidDataException("credentialGuessLimits.maxFailuresPerConnection must be between 1 and 1000");
        if (FailuresPerWindow is < 1 or > 100_000)
            throw new System.IO.InvalidDataException("credentialGuessLimits.failuresPerWindow must be between 1 and 100000");
        if (!double.IsFinite(WindowSeconds) || WindowSeconds is < 1 or > 86_400)
            throw new System.IO.InvalidDataException("credentialGuessLimits.windowSeconds must be finite and between 1 and 86400");
        if (!double.IsFinite(BaseLockoutSeconds) || BaseLockoutSeconds is < 0.1 or > 86_400)
            throw new System.IO.InvalidDataException("credentialGuessLimits.baseLockoutSeconds must be finite and between 0.1 and 86400");
        if (!double.IsFinite(MaxLockoutSeconds) || MaxLockoutSeconds < BaseLockoutSeconds || MaxLockoutSeconds > 86_400)
            throw new System.IO.InvalidDataException("credentialGuessLimits.maxLockoutSeconds must be finite, >= baseLockoutSeconds and <= 86400");
        if (MaxTrackedAddresses is < 1 or > 10_000_000)
            throw new System.IO.InvalidDataException("credentialGuessLimits.maxTrackedAddresses must be between 1 and 10000000");
    }
}

/// <summary>The outcome of one recorded credential failure (P1022 F7).</summary>
/// <param name="CloseConnection">This connection has reached
/// <see cref="CredentialGuessLimitsConfig.MaxFailuresPerConnection"/> and must be
/// retired (reported once, on the exact failure that reaches the limit).</param>
/// <param name="LockedOut">This failure started a source-address back-off.</param>
/// <param name="LockoutSeconds">The back-off length just started (0 when none).</param>
/// <param name="AddressFailures">The failure count inside the current window
/// (the count that triggered a back-off when <paramref name="LockedOut"/>).</param>
public readonly record struct CredentialFailure(
    bool CloseConnection, bool LockedOut, double LockoutSeconds, int AddressFailures);

/// <summary>
/// P1022 (R1 finding F7) — the credential-guessing counter itself: per
/// connection and per source address, with a doubling back-off, a bounded
/// table, and a caller-supplied clock so tests drive time without sleeping.
/// <para>Pure state: no logging, no sockets, no clock of its own. The server
/// calls it from the sim thread inside <c>ProcessInboundPacket</c> only, so it
/// needs no locking.</para>
/// <para><b>Correct credentials are never slowed:</b> only FAILED comparisons
/// are recorded (a right password/secret returns before any counting), and
/// <see cref="RecordSuccess"/> clears the connection's failures and the address's
/// current window, so a player who mistypes once and then logs in normally is
/// not penalised.</para>
/// </summary>
public sealed class CredentialGuessGuard
{
    private sealed class AddressState
    {
        public double WindowStart;
        public double LastFailure;
        public int Failures;
        public int Lockouts;
        public double LockedUntil;
        public long Touch;
    }

    private readonly CredentialGuessLimitsConfig _limits;
    private readonly Dictionary<ConnectionId, int> _connectionFailures = [];
    private readonly Dictionary<string, AddressState> _addresses = new(StringComparer.Ordinal);
    private long _touch;
    private long _evicted;

    public CredentialGuessGuard(CredentialGuessLimitsConfig limits) => _limits = limits;

    public CredentialGuessLimitsConfig Limits => _limits;

    /// <summary>Address entries currently held (bounded by
    /// <see cref="CredentialGuessLimitsConfig.MaxTrackedAddresses"/>).</summary>
    internal int TrackedAddressCount => _addresses.Count;

    /// <summary>Entries dropped because the table was full.</summary>
    internal long EvictedAddressCount => _evicted;

    /// <summary>
    /// True when this connection has already spent its failures, or its source
    /// address is inside a back-off at <paramref name="now"/>. The caller then
    /// does NOT evaluate the presented credential at all (no comparison, no
    /// reply) — the guessing oracle is closed for the duration.
    /// </summary>
    public bool IsLockedOut(ConnectionId peer, string address, double now)
    {
        if (!_limits.Enabled) return false;
        if (_connectionFailures.TryGetValue(peer, out var failures) && failures >= _limits.MaxFailuresPerConnection) return true;
        return _addresses.TryGetValue(address, out var state) && now < state.LockedUntil;
    }

    /// <summary>The connection-level half of <see cref="IsLockedOut"/>: this
    /// connection has spent <see cref="CredentialGuessLimitsConfig.MaxFailuresPerConnection"/>.</summary>
    public bool ConnectionExhausted(ConnectionId peer)
        => _limits.Enabled && _connectionFailures.TryGetValue(peer, out var failures)
           && failures >= _limits.MaxFailuresPerConnection;

    /// <summary>How long the address's current back-off still runs, in seconds
    /// (0 when it is not locked out at <paramref name="now"/>).</summary>
    public double LockoutRemaining(string address, double now)
        => _addresses.TryGetValue(address, out var state) && now < state.LockedUntil ? state.LockedUntil - now : 0;

    /// <summary>Record one FAILED credential comparison for
    /// (<paramref name="peer"/>, <paramref name="address"/>) at
    /// <paramref name="now"/> (seconds, monotonic).</summary>
    public CredentialFailure RecordFailure(ConnectionId peer, string address, double now)
    {
        if (!_limits.Enabled) return default;

        int connectionFailures = (_connectionFailures.TryGetValue(peer, out var current) ? current : 0) + 1;
        _connectionFailures[peer] = connectionFailures;
        bool close = connectionFailures == _limits.MaxFailuresPerConnection;

        if (!_addresses.TryGetValue(address, out var state))
        {
            EnsureCapacity(now);
            state = new AddressState { WindowStart = now, LastFailure = now };
            _addresses[address] = state;
        }
        else if (now - Math.Max(state.LastFailure, state.LockedUntil) >= _limits.WindowSeconds)
        {
            // A whole clean window since the last failure (and since any back-off
            // ended): the escalation restarts at the base duration.
            state.Lockouts = 0;
        }
        if (now - state.WindowStart >= _limits.WindowSeconds)
        {
            state.WindowStart = now;
            state.Failures = 0;
        }
        state.Failures++;
        state.LastFailure = now;
        state.Touch = ++_touch;

        bool locked = false;
        double lockoutSeconds = 0;
        int counted = state.Failures;
        if (state.Failures >= _limits.FailuresPerWindow)
        {
            state.Lockouts++;
            lockoutSeconds = Math.Min(_limits.BaseLockoutSeconds * Math.Pow(2, state.Lockouts - 1), _limits.MaxLockoutSeconds);
            state.LockedUntil = now + lockoutSeconds;
            state.Failures = 0;
            state.WindowStart = now;
            locked = true;
        }
        return new CredentialFailure(close, locked, lockoutSeconds, counted);
    }

    /// <summary>A CORRECT credential arrived: the CONNECTION's failure count is
    /// cleared (a peer that mistyped then got in is never retired later). The
    /// address's window is deliberately NOT cleared — a valid credential must
    /// not reset a NAT'd address's guessing history; the window expires on its
    /// own after <see cref="CredentialGuessLimitsConfig.WindowSeconds"/>.</summary>
    public void RecordSuccess(ConnectionId peer)
    {
        if (!_limits.Enabled) return;
        _connectionFailures.Remove(peer);
    }

    /// <summary>Drop the connection dimension with the peer (call on every
    /// release path, like the P654 chat budget). Address entries are NOT
    /// per-connection state and expire on their own.</summary>
    public void ForgetPeer(ConnectionId peer) => _connectionFailures.Remove(peer);

    /// <summary>Make room for one new address entry: prune expired entries
    /// first, then drop the least recently touched one. The table never exceeds
    /// <see cref="CredentialGuessLimitsConfig.MaxTrackedAddresses"/>.</summary>
    private void EnsureCapacity(double now)
    {
        if (_addresses.Count < _limits.MaxTrackedAddresses) return;
        foreach (var key in _addresses.Keys.ToArray())
        {
            if (!_addresses.TryGetValue(key, out var state)) continue;
            if (now >= state.LockedUntil && now - state.LastFailure >= _limits.WindowSeconds)
                if (_addresses.Remove(key)) _evicted++;
        }
        while (_addresses.Count >= _limits.MaxTrackedAddresses)
        {
            string? oldest = null;
            long oldestTouch = long.MaxValue;
            foreach (var entry in _addresses)
                if (entry.Value.Touch < oldestTouch) { oldestTouch = entry.Value.Touch; oldest = entry.Key; }
            if (oldest is null || !_addresses.Remove(oldest)) break;
            _evicted++;
        }
    }
}
