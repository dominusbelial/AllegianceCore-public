using System.Net;
using System.Text.Json.Serialization;

namespace Allegiance.Core.Transport;

public sealed record NatEndpoint(string Address, int Port)
{
    public IPEndPoint ToIPEndPoint()
    {
        if (Port is < 1 or > 65535 || !IPAddress.TryParse(Address, out var ip)) throw new IOException("Invalid lobby UDP endpoint");
        return new(ip, Port);
    }
    public static NatEndpoint From(IPEndPoint endpoint) => new(endpoint.Address.ToString(), endpoint.Port);
}
public sealed record NatConfiguration(bool PlayerHostedGames, int PrimaryPort, int SecondaryPort);
public sealed record PunchRequest(string Receipt);
public sealed record PunchPollRequest(string Token);
public sealed record PunchIntroduction(NatEndpoint Endpoint, string Secret, DateTimeOffset ExpiresUtc);
public sealed record PunchBatch(PunchIntroduction[] Introductions);
public sealed record PlayerHostRegistration(string Name, int Port, string? Token, string Receipt,
    int PlayerCount, int MaxPlayers, string CoreFile, int LobbyProtocolVersion = LobbyProtocol.Version, bool PlayerHosted = true);
public sealed record PlayerHostRegistered(string Id, string Token);
public sealed record InternetJoin(string LobbyUrl, string ListingId);

/// <summary>Relay selection can replace direct establishment at this boundary in a later task.</summary>
public sealed class DirectConnectionException : IOException
{
    public const string Advice = "Direct connection not possible on this network. Forward the TCP/UDP game port, or use a Tailscale address in Direct Connect.";
    public DirectConnectionException(Exception? inner = null) : base(Advice, inner) { }
}

[JsonSerializable(typeof(NatConfiguration))]
[JsonSerializable(typeof(PunchRequest))]
[JsonSerializable(typeof(PunchPollRequest))]
[JsonSerializable(typeof(PunchIntroduction))]
[JsonSerializable(typeof(PunchBatch))]
[JsonSerializable(typeof(PlayerHostRegistration))]
[JsonSerializable(typeof(PlayerHostRegistered))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
public partial class NatJsonContext : JsonSerializerContext;
