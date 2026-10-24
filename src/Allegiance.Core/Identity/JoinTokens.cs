using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.Core.Identity;

// Modern replacement for ZoneTicket. Native: Inc/Messages.h:36-51;
// FedSrv/FedSrv.CPP:5824-5904 validates the ticket and uses its canonical name/ID.
public sealed record JoinIdentity(string Iss, string Aud, string Sub, string Callsign, long Iat, long Exp, string Jti);

public static class JoinTokens
{
    private static readonly string Header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"ES256\",\"typ\":\"JWT\"}"));
    public const int LifetimeSeconds = 60;

    public static bool ValidCallsign(string? name) => name is { Length: >= 1 and <= 24 } && name[0] != ' ' && name[^1] != ' '
        && name.All(c => c is >= ' ' and <= '\u00ff' && !char.IsControl(c));

    public static string Issue(ECDsa key, string issuer, string audience, string accountId, string callsign, DateTimeOffset now)
    {
        if (!ValidCallsign(callsign) || !Guid.TryParseExact(accountId, "N", out _) || string.IsNullOrWhiteSpace(audience))
            throw new ArgumentException("Invalid identity");
        var claims = new JoinIdentity(issuer, audience, accountId, callsign, now.ToUnixTimeSeconds(),
            now.ToUnixTimeSeconds() + LifetimeSeconds, Guid.NewGuid().ToString("N"));
        string input = Header + "." + Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims, IdentityJsonContext.Default.JoinIdentity));
        return input + "." + Base64Url(key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public static JoinIdentity? Verify(string token, ECDsa key, string issuer, string audience, DateTimeOffset now)
    {
        try
        {
            if (token.Length > 2048) return null;
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
[JsonSerializable(typeof(DeviceStartRequest))]
[JsonSerializable(typeof(DeviceAuthorization))]
[JsonSerializable(typeof(DevicePollRequest))]
[JsonSerializable(typeof(DeviceResult))]
[JsonSerializable(typeof(IdentityAccount))]
[JsonSerializable(typeof(JoinTokenRequest))]
[JsonSerializable(typeof(JoinTokenResponse))]
[JsonSerializable(typeof(ServerEnrollment))]
public partial class IdentityJsonContext : JsonSerializerContext;
