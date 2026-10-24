using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Allegiance.Core.Identity;
using Allegiance.Lobby.Identity;
using System.Net.Http.Json;

namespace Allegiance.Lobby.Tests;

// A real ES256 WebAuthn authenticator, with none attestation and UP+UV flags.
// Tests cross the HTTP and Fido2 boundaries rather than replacing verification with a stub.
internal sealed class VirtualPasskey : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _id = RandomNumberGenerator.GetBytes(32);
    private byte[] _user = [];
    private uint _counter;
    private static string B64(byte[] b) => JoinTokens.Base64Url(b);
    public async Task<HttpResponseMessage> Register(HttpClient http, string callsign, string origin = "http://localhost:5000")
    {
        using var options = await http.PostAsJsonAsync("/api/identity/passkeys/register/options", new { callsign });
        options.EnsureSuccessStatusCode();
        var data = (await options.Content.ReadFromJsonAsync<PasskeyOptions>())!;
        using var json = JsonDocument.Parse(data.Options);
        var o = json.RootElement; _user = JoinTokens.FromBase64Url(o.GetProperty("user").GetProperty("id").GetString()!);
        byte[] client = JsonSerializer.SerializeToUtf8Bytes(new { type = "webauthn.create", challenge = o.GetProperty("challenge").GetString(), origin, crossOrigin = false });
        var p = _key.ExportParameters(false);
        var cose = new CborWriter(); cose.WriteStartMap(5);
        cose.WriteInt32(1); cose.WriteInt32(2); cose.WriteInt32(3); cose.WriteInt32(-7);
        cose.WriteInt32(-1); cose.WriteInt32(1); cose.WriteInt32(-2); cose.WriteByteString(p.Q.X!); cose.WriteInt32(-3); cose.WriteByteString(p.Q.Y!); cose.WriteEndMap();
        byte[] auth = new byte[37 + 16 + 2 + _id.Length + cose.BytesWritten];
        SHA256.HashData(Encoding.UTF8.GetBytes("localhost")).CopyTo(auth, 0);
        auth[32] = 0x45; // user present, user verified, attested credential
        BinaryPrimitives.WriteUInt16BigEndian(auth.AsSpan(53), (ushort)_id.Length);
        _id.CopyTo(auth, 55); cose.Encode().CopyTo(auth, 55 + _id.Length);
        var att = new CborWriter(); att.WriteStartMap(3); att.WriteTextString("fmt"); att.WriteTextString("none");
        att.WriteTextString("attStmt"); att.WriteStartMap(0); att.WriteEndMap();
        att.WriteTextString("authData"); att.WriteByteString(auth); att.WriteEndMap();
        return await http.PostAsJsonAsync("/api/identity/passkeys/register", new
        {
            challenge = data.Challenge,
            credential = new { id = B64(_id), rawId = B64(_id), type = "public-key", response = new { clientDataJSON = B64(client), attestationObject = B64(att.Encode()), transports = new[] { "internal" } }, extensions = new { } },
        });
    }
    public async Task<HttpResponseMessage> Login(HttpClient http, string origin = "http://localhost:5000", bool forge = false)
    {
        using var options = await http.PostAsJsonAsync("/api/identity/passkeys/login/options", new { }); options.EnsureSuccessStatusCode();
        var data = (await options.Content.ReadFromJsonAsync<PasskeyOptions>())!;
        using var json = JsonDocument.Parse(data.Options);
        byte[] client = JsonSerializer.SerializeToUtf8Bytes(new { type = "webauthn.get", challenge = json.RootElement.GetProperty("challenge").GetString(), origin, crossOrigin = false });
        byte[] auth = new byte[37]; SHA256.HashData(Encoding.UTF8.GetBytes("localhost")).CopyTo(auth, 0);
        auth[32] = 5; BinaryPrimitives.WriteUInt32BigEndian(auth.AsSpan(33), ++_counter);
        byte[] signed = [.. auth, .. SHA256.HashData(client)];
        byte[] signature = _key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        if (forge) signature[^1] ^= 1;
        return await http.PostAsJsonAsync("/api/identity/passkeys/login", new
        {
            challenge = data.Challenge,
            credential = new { id = B64(_id), rawId = B64(_id), type = "public-key", response = new { clientDataJSON = B64(client), authenticatorData = B64(auth), signature = B64(signature), userHandle = B64(_user) }, extensions = new { } },
        });
    }
    public void Dispose() => _key.Dispose();
}
