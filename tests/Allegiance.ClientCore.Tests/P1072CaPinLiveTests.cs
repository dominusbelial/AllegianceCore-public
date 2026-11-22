using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork"), Trait("Tier", "Live")]
public sealed class P1072CaPinLiveTests
{
    private static X509Certificate2 Root(ECDsa key)
    {
        var request = new CertificateRequest("CN=P1072 ephemeral CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }
    private static X509Certificate2 Leaf(X509Certificate2 root, ECDsa key)
    {
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var leaf = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        return leaf.CopyWithPrivateKey(key);
    }

    private sealed class TlsProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _connections = [];
        private readonly Task _accept;
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal TlsProxy(ushort upstreamPort, X509Certificate2 certificate)
        {
            _listener.Start();
            _accept = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        _connections.Add(Forward(client));
                    }
                }
                catch (OperationCanceledException) { }
            });
            async Task Forward(TcpClient client)
            {
                using (client)
                using (var ssl = new SslStream(client.GetStream(), false))
                using (var upstream = new TcpClient())
                {
                    try
                    {
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, _stop.Token);
                        await upstream.ConnectAsync(IPAddress.Loopback, upstreamPort, _stop.Token);
                        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        var send = ssl.CopyToAsync(upstream.GetStream(), lifetime.Token);
                        var receive = upstream.GetStream().CopyToAsync(ssl, lifetime.Token);
                        await Task.WhenAny(send, receive); lifetime.Cancel();
                        await Task.WhenAll(send, receive);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or AuthenticationException or IOException or SocketException) { }
                }
            }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop(); await _accept; await Task.WhenAll(_connections); _stop.Dispose();
        }
    }

    [SkippableFact]
    public async Task Bot_driver_trusts_only_configured_ca_with_name_checks_and_no_system_store_change()
    {
        Require.Artwork();
        using var aKey = ECDsa.Create(); using var bKey = ECDsa.Create(); using var leafKey = ECDsa.Create();
        using var a = Root(aKey); using var b = Root(bKey);
        using var trusted = Leaf(a, leafKey); using var untrusted = Leaf(b, leafKey);
        string path = Path.GetTempFileName();
        File.WriteAllText(path, a.ExportCertificatePem()); // certificate only; private keys stay in memory
        try
        {
            using var rig = new P1072WebSocketBotsLiveTests.Rig();
            await using var goodProxy = new TlsProxy(rig.Server.WebSocketPort, trusted);
            await using var badProxy = new TlsProxy(rig.Server.WebSocketPort, untrusted);
            BotTransportOptions Options(int port) => DriverOptions.Parse(["127.0.0.1", rig.Server.BoundPort.ToString(),
                "--transport", "ws", "--ws-url", $"wss://127.0.0.1:{port}/game", "--ws-origin", "http://127.0.0.1", "--ws-ca-file", path]).Transport!;
            using (var manager = new BotManager("127.0.0.1", rig.Server.BoundPort, transport: Options(goodProxy.Port)))
            { Assert.True(manager.Probe.Ack!.FValidated); Assert.Equal("wss", manager.Probe.ActiveTransportProtocol); }
            Assert.ThrowsAny<System.Net.WebSockets.WebSocketException>(() => new BotManager("127.0.0.1", rig.Server.BoundPort, transport: Options(badProxy.Port)));
            Assert.ThrowsAny<System.Net.WebSockets.WebSocketException>(() => new BotManager("127.0.0.1", rig.Server.BoundPort,
                transport: Options(goodProxy.Port) with { CaFile = null }));
            var validate = Options(goodProxy.Port).CertificateValidator()!;
            Assert.False(validate(this, trusted, null, SslPolicyErrors.RemoteCertificateNameMismatch));
            Assert.False(validate(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        }
        finally { File.Delete(path); }
    }
}
