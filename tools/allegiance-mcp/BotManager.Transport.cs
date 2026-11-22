using Allegiance.ClientCore.Session;
using Allegiance.Core.Transport;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private readonly BotTransportOptions _botTransport;
    private int _transportBotOffset;

    private void ConnectBotTransport(ITransportSite client, string name, string host, ushort port, long botIndex = 0)
    {
        long index = botIndex * (Partition?.Count ?? 1) + (Partition?.Index ?? 0);
        var options = _botTransport with { Protocol = _botTransport.ProtocolAt(index) };
        if (client is ConnectionSession cs)
        {
            cs.TransportProtocol = options.Protocol == "ws" ? "tcp" : options.Protocol;
            cs.WebSocketEndpoint = options.Protocol == "ws" ? WebSocketEndpoint.FromConfiguration(options.Url!) : null;
            cs.WebSocketOrigin = options.Origin;
            cs.WebSocketCertificateValidation = options.CertificateValidator();
        }
        _stallTelemetry.Connect(client, name, host, port, options);
        string protocol = options.Protocol;
        string route = protocol == "ws" ? $"{options.Url} (origin {options.Origin})" : $"{host}:{port}";
        Console.WriteLine($"[transport] {name} {protocol} {route} index={index}");
    }

    internal (string Name, ConnectionSession Session)[] TransportClients => _stallTelemetry.ConnectedClients;
    internal object[] TransportStatus() => TransportClients.Select(p => (object)new
        { name = p.Name, protocol = p.Session.ActiveTransportProtocol, reconnects = p.Session.ResumeAttempts }).ToArray();

    private static string BotProtocol(ActiveBot bot) => bot.Cs?.ActiveTransportProtocol
        ?? (bot.Bot is { } plain && ObservedSession.PlainTransport(plain) is { } transport
            ? TransportFactory.ActiveTransport(transport).ProtocolName : "disconnected");
}
