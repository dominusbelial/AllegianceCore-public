using System.Security.Cryptography;
using Allegiance.Core.Identity;
using Allegiance.Lobby.Squad;

namespace Allegiance.Lobby.Identity;

public sealed class IdentitySigningKey : IDisposable
{
    private readonly ECDsa _key;
    private readonly IdentitySettings _settings;
    private readonly SquadStore _squads;
    private readonly object _gate = new();
    public string PublicKey { get; }
    public IdentitySigningKey(IdentitySettings settings, SquadStore squads)
    {
        _settings = settings;
        _squads = squads;
        string path = Path.GetFullPath(settings.SigningKeyFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (!File.Exists(path))
        {
            // CreateNew never replaces another process's key. Restrict access at creation.
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var file = new FileStream(path, options);
                using var writer = new StreamWriter(file);
                writer.Write(_key.ExportPkcs8PrivateKeyPem());
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        _key.ImportFromPem(File.ReadAllText(path));
        if (_key.KeySize != 256) throw new InvalidOperationException("Identity requires a P-256 signing key");
        PublicKey = _key.ExportSubjectPublicKeyInfoPem();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settings.PublicKeyFile))!);
        File.WriteAllText(settings.PublicKeyFile, PublicKey);
    }
    public string Issue(IdentityAccount account, string serverId, DateTimeOffset now)
    {
        // P820b — the player's active squad memberships ride the join token so
        // the game server can gate a side's squad (native ZoneTicket carried the
        // account's club data; here the lobby stamps the squad claims directly).
        IReadOnlyList<SquadClaim>? squads = null;
        try
        {
            var claims = _squads.SquadClaimsFor(account.Id);
            if (claims.Count > 0)
                squads = claims.Select(c => new SquadClaim(c.Id, c.Name, c.CanLead)).ToList();
        }
        catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException)
        {
            squads = null;   // a store hiccup must not block joining; squads just arrive empty
        }
        lock (_gate) return JoinTokens.Issue(_key, _settings.PublicUrl, serverId, account.Id, account.Callsign, now, squads);
    }
    public void Dispose() => _key.Dispose();
}
