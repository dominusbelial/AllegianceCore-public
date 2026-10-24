using System.Text;
using Allegiance.Core.Identity;
using Fido2NetLib;
using Fido2NetLib.Objects;

namespace Allegiance.Lobby.Identity;

public sealed class PasskeyService
{
    private readonly Fido2 _fido;
    private readonly IdentityStore _store;
    public PasskeyService(IdentitySettings settings, IdentityStore store)
    {
        _store = store;
        _fido = new Fido2(new Fido2Configuration
        {
            ServerDomain = new Uri(settings.PublicUrl).Host,
            ServerName = "Allegiance",
            Origins = new HashSet<string> { settings.PublicUrl },
        });
    }
    public PasskeyOptions RegisterOptions(string callsign)
    {
        if (!JoinTokens.ValidCallsign(callsign)) throw new ArgumentException("Callsign must be 1–24 printable Latin-1 characters, without outer spaces");
        string id = Guid.NewGuid().ToString("N");
        var options = _fido.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Name = callsign, DisplayName = callsign, Id = Encoding.UTF8.GetBytes(id) },
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
            ExcludeCredentials = [],
        });
        return new(_store.Challenge("register", options.ToJson()), options.ToJson());
    }
    public async Task<IdentityAccount> Register(string challenge, AuthenticatorAttestationRawResponse response, CancellationToken ct)
    {
        var json = _store.ConsumeChallenge(challenge, "register") ?? throw new ArgumentException("Challenge expired or already used");
        var options = CredentialCreateOptions.FromJson(json);
        var credential = await _fido.MakeNewCredentialAsync(new MakeNewCredentialParams
        {
            AttestationResponse = response, OriginalOptions = options,
            IsCredentialIdUniqueToUserCallback = (args, _) => Task.FromResult(_store.Row("SELECT id FROM credentials WHERE id=@p0", JoinTokens.Base64Url(args.CredentialId)) is null),
        }, ct);
        return _store.CreatePasskeyAccount(Encoding.UTF8.GetString(options.User.Id), options.User.Name,
            JoinTokens.Base64Url(credential.Id), Convert.ToBase64String(credential.PublicKey), credential.SignCount);
    }
    public PasskeyOptions LoginOptions()
    {
        var options = _fido.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [], UserVerification = UserVerificationRequirement.Required,
        });
        return new(_store.Challenge("login", options.ToJson()), options.ToJson());
    }
    public async Task<IdentityAccount> Login(string challenge, AuthenticatorAssertionRawResponse response, CancellationToken ct)
    {
        var json = _store.ConsumeChallenge(challenge, "login") ?? throw new ArgumentException("Challenge expired or already used");
        var id = JoinTokens.Base64Url(response.RawId);
        var r = _store.Row("SELECT account_id,public_key,counter FROM credentials WHERE id=@p0", id) ?? throw new ArgumentException("Unknown credential");
        var result = await _fido.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = response, OriginalOptions = AssertionOptions.FromJson(json),
            StoredPublicKey = Convert.FromBase64String(r[1]), StoredSignatureCounter = uint.Parse(r[2], System.Globalization.CultureInfo.InvariantCulture),
            IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(args.UserHandle.SequenceEqual(Encoding.UTF8.GetBytes(r[0])) && args.CredentialId.SequenceEqual(response.RawId)),
        }, ct);
        if (_store.Execute("UPDATE credentials SET counter=@p0 WHERE id=@p1 AND counter=@p2", (long)result.SignCount, id, long.Parse(r[2], System.Globalization.CultureInfo.InvariantCulture)) != 1)
            throw new ArgumentException("Concurrent passkey use; please sign in again");
        return _store.Account(r[0])!;
    }
}

public sealed record PasskeyOptions(string Challenge, string Options);
public sealed record RegisterOptionsRequest(string Callsign);
public sealed record RegisterPasskeyRequest(string Challenge, AuthenticatorAttestationRawResponse Credential);
public sealed record LoginPasskeyRequest(string Challenge, AuthenticatorAssertionRawResponse Credential);
