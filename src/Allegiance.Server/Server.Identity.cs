using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Allegiance.Core.Identity;
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
    private readonly Dictionary<ConnectionId, JoinIdentity> _peerIdentities = new();
    private readonly Dictionary<string, long> _consumedIdentityTokens = new(StringComparer.Ordinal);
    public string? AccountId(ConnectionId peer) => _peerIdentities.GetValueOrDefault(peer)?.Sub;

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
        if (req.ZoneTicket.Length > 2048) return false;
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
