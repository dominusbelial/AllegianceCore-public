using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Allegiance.Core.Identity;
using Allegiance.Core.Model;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed class ServerIdentityConfig
{
    public string Issuer { get; set; } = "";
    public string PublicKeyFile { get; set; } = "";
    public string Audience { get; set; } = "";
    public bool AllowAnonymous { get; set; } = true;
}

public partial class Server
{
    /// <summary>Native CFSMission::RemovePlayerByName (fsmission.cpp:1248-1273):
    /// first case-insensitive player match in this mission, normal removal and notice.
    /// The caller authenticates authority before entering this primitive.</summary>
    private bool RemovePlayerByName(Mission mission, string name, QuitSideReason reason, string message = "")
    {
        var target = _peerShips.FirstOrDefault(p => ReferenceEquals(p.Value.Ship.Mission, mission)
            && _peerNames.TryGetValue(p.Key, out var playerName)
            && string.Equals(playerName, name, StringComparison.OrdinalIgnoreCase));
        if (target.Value is null) return false;
        // RemovePlayerFromMission sends QUIT_MISSION to the leaver as well as
        // survivors (fsmission.cpp:482-488). A boot must bypass reconnect parking.
        SendPeer(target.Key, FedPacket.Pack([new QuitMission
        { ShipId = target.Value.Ship.Id, Reason = reason, Message = message }.Encode()]));
        ReleasePeerWithReason(target.Key, reason, requested: true, message: message);
        return true;
    }

    private readonly Dictionary<ConnectionId, JoinIdentity> _peerIdentities = new();
    private readonly Dictionary<string, long> _consumedIdentityTokens = new(StringComparer.Ordinal);
    public string? AccountId(ConnectionId peer) => _peerIdentities.GetValueOrDefault(peer)?.Sub;

    /// <summary>P820b — a verified peer's active squad memberships (from the
    /// join token's <c>Squads</c> claim), or <c>null</c> when the peer has no
    /// identity/token squads (anonymous, bot, or none).</summary>
    private IReadOnlyList<SquadClaim>? PeerSquads(ConnectionId peer) => _peerIdentities.GetValueOrDefault(peer)?.Squads;

    /// <summary>P820b — native <c>CFSPlayer::GetCanLeadSquad</c>
    /// (fsship.cpp:1374-1390): whether the peer is leader or ASL of the side's
    /// squad. A null <paramref name="squadId"/> (no squad) means anyone can lead.</summary>
    private bool PeerCanLeadSquad(ConnectionId peer, string? squadId)
    {
        if (squadId is null) return true;
        return PeerSquads(peer)?.Any(s => string.Equals(s.Id, squadId, StringComparison.Ordinal) && s.CanLead) ?? false;
    }

    /// <summary>P820b — native <c>CFSPlayer::GetIsMemberOfSquad</c>
    /// (fsship.cpp:1358-1373): whether the peer is an active member of the
    /// squad. A null <paramref name="squadId"/> (no squad) is everyone's member.</summary>
    private bool PeerIsMemberOfSquad(ConnectionId peer, string? squadId)
    {
        if (squadId is null) return true;
        return PeerSquads(peer)?.Any(s => string.Equals(s.Id, squadId, StringComparison.Ordinal)) ?? false;
    }

    /// <summary>P820b — native <c>CFSPlayer::GetPreferredSquadToLead</c>
    /// (fsship.cpp:1397+): the squad the player leads, else their first active
    /// squad (for a peer with no identity/squads, <c>null</c>).</summary>
    private (string? Id, string? Name)? PeerPreferredSquad(ConnectionId peer)
    {
        var squads = PeerSquads(peer);
        if (squads is null || squads.Count == 0) return null;
        var led = squads.FirstOrDefault(s => s.CanLead);
        var chosen = led is { Id: not null } ? led : squads[0];
        return (chosen.Id, chosen.Name);
    }

    private void ValidateIdentityConfiguration()
    {
        if (_config.Lobby?.EnrollmentFile is { Length: > 0 } && _config.Identity is null)
            throw new InvalidDataException("Server enrollment requires pinned identity configuration");
        if (_config.Identity is not { } identity) return;
        if (string.IsNullOrWhiteSpace(identity.Issuer) || string.IsNullOrWhiteSpace(identity.PublicKeyFile)
            || (string.IsNullOrWhiteSpace(identity.Audience) && string.IsNullOrWhiteSpace(_config.Lobby?.EnrollmentFile)))
            throw new InvalidDataException("Identity requires issuer, publicKeyFile and audience (or enrollmentFile)");
        using var key = ECDsa.Create(); key.ImportFromPem(File.ReadAllText(identity.PublicKeyFile));
        if (key.KeySize != 256) throw new InvalidDataException("Identity requires a P-256 public key");
    }

    // Native's USEAUTH branch trusts the issuer, validates current entitlement and
    // binds the canonical name/account ID (FedSrv/FedSrv.CPP:5834-5878).
    private bool BindIdentity(ConnectionId peer, LogonRequest req, out string failure)
    {
        failure = "invalid or expired lobby join token";
        var settings = _config.Identity;
        if (settings is null) return true; // native standalone ignores opaque legacy tickets
        if (req.ZoneTicket is not { Length: > 0 })
        {
            failure = "this server requires lobby sign-in";
            return settings.AllowAnonymous;
        }
        if (req.ZoneTicket.Length > JoinTokens.MaxTokenLength) return false;
        try
        {
            string audience = settings.Audience;
            if (audience.Length == 0 && _config.Lobby?.EnrollmentFile is { Length: > 0 } path && File.Exists(path))
                audience = JsonSerializer.Deserialize(File.ReadAllText(path), IdentityJsonContext.Default.ServerEnrollment)?.ServerId ?? "";
            if (audience.Length == 0) return false;
            using var key = ECDsa.Create(); key.ImportFromPem(File.ReadAllText(settings.PublicKeyFile));
            var now = DateTimeOffset.UtcNow;
            var claims = JoinTokens.Verify(Encoding.UTF8.GetString(req.ZoneTicket), key, settings.Issuer, audience, now);
            if (claims is null) return false;
            foreach (string expired in _consumedIdentityTokens.Where(p => p.Value <= now.ToUnixTimeSeconds()).Select(p => p.Key).ToArray())
                _consumedIdentityTokens.Remove(expired);
            if (!_consumedIdentityTokens.TryAdd(claims.Jti, claims.Exp)) { failure = "lobby join token already used"; return false; }
            req.CharacterName = claims.Callsign;
            _peerIdentities[peer] = claims;
            return true;
        }
        catch (Exception e) when (e is IOException or CryptographicException or JsonException or ArgumentException) { return false; }
    }
    // Native LOGONACK carries the accepted character name on success; the client
    // keeps it as its own name (clintlib/appmsg.cpp:39-40). A verified logon's name
    // is the account's canonical callsign (BindIdentity replaced it).
    private static string IdentityWelcome(ConnectionId peer, string? name) => name ?? "";
}
