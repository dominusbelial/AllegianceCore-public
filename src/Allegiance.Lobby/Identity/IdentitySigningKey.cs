using System.Security.Cryptography;
using Allegiance.Core.Identity;

namespace Allegiance.Lobby.Identity;

public sealed class IdentitySigningKey : IDisposable
{
    private readonly ECDsa _key;
    private readonly IdentitySettings _settings;
    private readonly object _gate = new();
    public string PublicKey { get; }
    public IdentitySigningKey(IdentitySettings settings)
    {
        _settings = settings;
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
        lock (_gate) return JoinTokens.Issue(_key, _settings.PublicUrl, serverId, account.Id, account.Callsign, now);
    }
    public void Dispose() => _key.Dispose();
}
