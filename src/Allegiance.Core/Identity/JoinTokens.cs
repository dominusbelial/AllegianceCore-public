using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.Core.Identity;

// Modern replacement for ZoneTicket. Native: Inc/Messages.h:36-51;
// FedSrv/FedSrv.CPP:5824-5904 validates the ticket and uses its canonical name/ID.
// <c>Squads</c> (P820b) carries the player's active squad memberships so they
// reach the game server through this same identity path (native's SetSideSquad
// gates on GetIsMemberOfSquad / GetCanLeadSquad, fsmission.cpp:1612-1635,
// fsship.cpp:1358-1390). Legacy tokens without the claim verify unchanged
// (Squads reads null).
public sealed record JoinIdentity(string Iss, string Aud, string Sub, string Callsign, long Iat, long Exp, string Jti,
    IReadOnlyList<SquadClaim>? Squads = null);

/// <summary>A squad membership riding the join token (P820b). <c>CanLead</c> is
/// the native <c>GetCanLeadSquad</c> result (leader or ASL, fsship.cpp:1374-1390);
/// the id/name let the server derive a side's squad and relay it to the client.</summary>
public sealed record SquadClaim(string Id, string Name, bool CanLead);

public static class JoinTokens
{
    private static readonly string Header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"ES256\",\"typ\":\"JWT\"}"));
    public const int LifetimeSeconds = 60;

    /// <summary>
    /// P820c — the per-token cap on squad memberships (P820b fix 5). Native has
    /// no explicit small per-player squad limit (the membership list is a
    /// DB-loaded <c>TList&lt;SquadMembership&gt;</c>, fsship.h:383-385,536; the
    /// lobby database decides), so this is a documented small bound of 16 —
    /// far beyond any realistic active membership (a player leads or belongs to
    /// a handful of squads). The lobby's <see cref="SquadStore.SquadClaimsFor"/>
    /// caps its SELECT at this many active memberships.
    /// </summary>
    public const int MaxSquadsPerToken = 16;

    /// <summary>
    /// P820c — the join-token length cap (P820b fix 5). P820b raised it from
    /// 2048 to 65536 (the transport's frame cap) with no bound on squad claims.
    /// With squad memberships capped at <see cref="MaxSquadsPerToken"/>, the
    /// worst-case token — 16 claims, each with a max-length (24-char, possibly
    /// JSON-escaped) squad name — is ≈2.9 kB. This cap of 5728 bytes gives
    /// roughly 2× that margin while staying well under the 8192-byte
    /// single-message comfort line (and far under the 65536 transport frame
    /// cap), so a maximal legitimate token verifies and anything larger is
    /// rejected outright.
    /// </summary>
    public const int MaxTokenLength = 5728;

    public static bool ValidCallsign(string? name) => name is { Length: >= 1 and <= 24 } && name[0] != ' ' && name[^1] != ' '
        && name.All(c => c is >= ' ' and <= '\u00ff' && !char.IsControl(c));

    public static string Issue(ECDsa key, string issuer, string audience, string accountId, string callsign, DateTimeOffset now,
        IReadOnlyList<SquadClaim>? squads = null)
    {
        if (!ValidCallsign(callsign) || !Guid.TryParseExact(accountId, "N", out _) || string.IsNullOrWhiteSpace(audience))
            throw new ArgumentException("Invalid identity");
        var claims = new JoinIdentity(issuer, audience, accountId, callsign, now.ToUnixTimeSeconds(),
            now.ToUnixTimeSeconds() + LifetimeSeconds, Guid.NewGuid().ToString("N"), squads);
        string input = Header + "." + Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims, IdentityJsonContext.Default.JoinIdentity));
        return input + "." + Base64Url(key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public static JoinIdentity? Verify(string token, ECDsa key, string issuer, string audience, DateTimeOffset now)
    {
        try
        {
            if (token.Length > MaxTokenLength) return null;
            var parts = token.Split('.');
            if (parts.Length != 3 || parts[0] != Header) return null;
            var signature = FromBase64Url(parts[2]);
            if (signature.Length != 64 || !key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature,
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return null;
            var c = JsonSerializer.Deserialize(FromBase64Url(parts[1]), IdentityJsonContext.Default.JoinIdentity);
            long seconds = now.ToUnixTimeSeconds();
            return c is not null && c.Iss == issuer && c.Aud == audience && c.Exp > seconds && c.Iat <= seconds + 5
                && c.Exp > c.Iat && c.Exp - c.Iat <= LifetimeSeconds && ValidCallsign(c.Callsign)
                && Guid.TryParseExact(c.Sub, "N", out _) && Guid.TryParseExact(c.Jti, "N", out _) ? c : null;
        }
        catch (Exception e) when (e is FormatException or CryptographicException or JsonException or ArgumentException) { return null; }
    }

    public static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] FromBase64Url(string value)
    {
        if (value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new FormatException();
        string s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '='));
    }
}

public sealed record DeviceStartRequest(string Purpose, string Name);
public sealed record DeviceAuthorization(string DeviceCode, string UserCode, string VerificationUri, int ExpiresIn, int Interval);
public sealed record DevicePollRequest(string DeviceCode);
public sealed record IdentityAccount(string Id, string Callsign);
public sealed record DeviceResult(string Status, string? Session = null, IdentityAccount? Account = null,
    string? ServerId = null, string? ServerSecret = null);
public sealed record JoinTokenRequest(string ServerId);
public sealed record JoinTokenResponse(string Token);
public sealed record ServerEnrollment(string ServerId, string ServerSecret);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(JoinIdentity))]
[JsonSerializable(typeof(SquadClaim))]
[JsonSerializable(typeof(List<SquadClaim>))]
[JsonSerializable(typeof(DeviceStartRequest))]
[JsonSerializable(typeof(DeviceAuthorization))]
[JsonSerializable(typeof(DevicePollRequest))]
[JsonSerializable(typeof(DeviceResult))]
[JsonSerializable(typeof(IdentityAccount))]
[JsonSerializable(typeof(JoinTokenRequest))]
[JsonSerializable(typeof(JoinTokenResponse))]
[JsonSerializable(typeof(ServerEnrollment))]
public partial class IdentityJsonContext : JsonSerializerContext;
