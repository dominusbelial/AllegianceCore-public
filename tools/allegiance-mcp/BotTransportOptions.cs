using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Allegiance.Core.Transport;

namespace Allegiance.Tools.McpSpawner;

/// <summary>Operator-owned route, shared by all workers. No implicit WS fallback.</summary>
internal sealed record BotTransportOptions(string Protocol = "tcp", string? Url = null, string? Origin = null,
    double? Fraction = null, string? CaFile = null)
{
    internal static BotTransportOptions Read(Func<string, string?> env) => new(
        env("ALLEGIANCE_BOT_TRANSPORT") ?? env("ALLEGIANCE_TRANSPORT_PROTOCOL") ?? "tcp", env("ALLEGIANCE_BOT_WS_URL"), env("ALLEGIANCE_BOT_WS_ORIGIN"),
        env("ALLEGIANCE_BOT_WS_FRACTION") is { } f ? ParseFraction(f) : null, env("ALLEGIANCE_BOT_WS_CA_FILE"));

    internal static double ParseFraction(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
        && double.IsFinite(f) && f is >= 0 and <= 1 ? f : throw new ArgumentException("--ws-fraction requires 0..1");

    internal BotTransportOptions Validate()
    {
        if (Protocol is not ("tcp" or "udp" or "ws")) throw new ArgumentException("--transport requires tcp, udp or ws");
        if (Fraction is { } f && (!double.IsFinite(f) || f is < 0 or > 1)) throw new ArgumentException("--ws-fraction requires 0..1");
        if (Protocol == "ws" || Fraction is > 0)
        {
            _ = WebSocketEndpoint.FromConfiguration(Url ?? throw new ArgumentException("--ws-url is required for WebSocket bots"));
            if (!Uri.TryCreate(Origin, UriKind.Absolute, out var origin) || origin.UserInfo.Length != 0 || origin.Query.Length != 0
                || origin.Fragment.Length != 0 || origin.AbsolutePath != "/" || Origin != origin.GetLeftPart(UriPartial.Authority)
                || !(origin.Scheme == "https" || origin.Scheme == "http" && origin.IsLoopback))
                throw new ArgumentException("--ws-origin requires a canonical HTTPS origin (HTTP allowed on loopback)");
        }
        if (CaFile is not null) { using var ca = LoadCa(CaFile); }
        return this;
    }

    // Balanced deterministic sequence: exactly floor(n*f) WS sessions in the
    // first n indices, independent of worker scheduling and random bot names.
    internal bool UsesWebSocket(long index)
    {
        double f = Fraction ?? (Protocol == "ws" ? 1 : 0);
        return Math.Floor((index + 1) * f) > Math.Floor(index * f);
    }
    internal string ProtocolAt(long index) => UsesWebSocket(index) ? "ws" : Fraction is not null || Protocol == "ws" ? "tcp" : Protocol;
    internal IEnumerable<string> Arguments()
    {
        yield return "--transport"; yield return Protocol;
        if (Url is not null) { yield return "--ws-url"; yield return Url; }
        if (Origin is not null) { yield return "--ws-origin"; yield return Origin; }
        if (Fraction is { } f) { yield return "--ws-fraction"; yield return f.ToString("R", CultureInfo.InvariantCulture); }
        if (CaFile is not null) { yield return "--ws-ca-file"; yield return CaFile; }
    }

    private static X509Certificate2 LoadCa(string path)
    {
        X509Certificate2 ca;
        try { ca = X509Certificate2.CreateFromPem(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { throw new ArgumentException("--ws-ca-file must be a readable PEM CA certificate", ex); }
        if (!ca.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority))
        { ca.Dispose(); throw new ArgumentException("--ws-ca-file must contain a CA certificate"); }
        return ca;
    }
    internal RemoteCertificateValidationCallback? CertificateValidator()
    {
        if (CaFile is null) return null;
        using var ca = LoadCa(CaFile);
        byte[] root = ca.RawData;
        return (_, certificate, suppliedChain, errors) =>
        {
            // Name validation is still the TLS stack's responsibility. Replace
            // only chain trust, using exactly the configured root and serverAuth.
            if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
            using var trusted = X509CertificateLoader.LoadCertificate(root);
            using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(trusted);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new("1.3.6.1.5.5.7.3.1"));
            if (suppliedChain is not null)
                foreach (var element in suppliedChain.ChainElements) chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            return chain.Build(leaf) && chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(root);
        };
    }
}
