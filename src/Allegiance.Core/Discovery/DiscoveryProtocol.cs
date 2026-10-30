using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.Core.Discovery;

/// <summary>
/// P731 — the UDP discovery protocol spoken between the client's "Find Server"
/// intro flow and the server's discovery responder.
/// <para>
/// Rationale for a tiny JSON datagram protocol instead of a bespoke binary
/// format: the payload is one short UDP datagram each way, it is trivially
/// debuggable with <c>socat</c>/<c>nc -u</c>, it carries no secrets, and the
/// client and server each already treat their counterpart's DTOs as a
/// documented, case-insensitive JSON contract (the same deliberate decoupling
/// as <see cref="Allegiance.ClientCore.MonoGame.ServerListing"/> mirroring the
/// Lobby's DTO). This record is the single shared declaration so the two sides
/// can never drift apart; both projects reference <c>Allegiance.Core</c>.</para>
/// <para>
/// Request: one datagram from the searcher to the responder's UDP port —
/// broadcast, multicast, or a unicast to a scanned peer. Carries a random
/// <see cref="Request.Nonce"/> the responder echoes so the searcher can match a
/// reply to its own outstanding query (essential when a subnet/Tailscale scan
/// is firing hundreds of requests whose replies all arrive on one socket).</para>
/// <para>
/// Reply: one datagram back to the sender with the server's public facts
/// (name, map, players/max, version, game port). A non-zero
/// <see cref="Reply.GamePort"/> tells the client which TCP port to connect to —
/// the discovery responder port itself is <em>not</em> the game port, so the
/// two are kept distinct in the payload.</para>
/// </summary>
public static class DiscoveryProtocol
{
    /// <summary>Magic string each request carries so a stray datagram hitting
    /// the responder port cannot accidentally provoke a reply.</summary>
    public const string Magic = "allegiance-disc";

    public const int Version = 1;

    public static readonly Encoding Utf8 = Encoding.UTF8;

    /// <summary>Case-insensitive, unindented options backed by the source-generated
    /// <see cref="DiscoveryJsonContext"/> (the server publishes with native AOT, which
    /// rejects reflection-based serialization).</summary>
    public static JsonSerializerOptions JsonOptions => DiscoveryJsonContext.Default.Options;

    /// <summary>The client's one-datagram query.</summary>
    public sealed class Request
    {
        public string Magic { get; set; } = DiscoveryProtocol.Magic;
        public int V { get; set; } = Version;

        /// <summary>Random hex string the searcher generates per request; the
        /// responder echoes it verbatim in <see cref="Reply.Nonce"/> so the
        /// searcher can correlate replies (see the class doc).</summary>
        public string Nonce { get; set; } = "";
    }

    /// <summary>The responder's one-datagram answer.</summary>
    public sealed class Reply
    {
        public string Magic { get; set; } = DiscoveryProtocol.Magic;
        public int V { get; set; } = DiscoveryProtocol.Version;

        /// <summary>Echoed request nonce (empty when this is an unsolicited
        /// announcement, which this responder never sends).</summary>
        public string Nonce { get; set; } = "";

        /// <summary>Human-readable server/mission name.</summary>
        public string Name { get; set; } = "";

        /// <summary>Mission map file name, when known.</summary>
        public string Map { get; set; } = "";

        /// <summary>Current number of players seated in the mission(s).</summary>
        public int Players { get; set; }

        /// <summary>Configured player capacity (0 = unknown/unlimited).</summary>
        public int MaxPlayers { get; set; }

        /// <summary>Port the responder's own game lives on.</summary>
        public int GamePort { get; set; }

        /// <summary>Version string the server advertises (its build/tag).</summary>
        public string Version { get; set; } = "";

        /// <summary>The responder's bound UDP port (informational, so a client
        /// that hit a forwarded/broadcast port can see where the reply came from).</summary>
        public int ResponderPort { get; set; }

        /// <summary>Optional P897 game UDP listener; absent on older/TCP-only servers.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? UdpPort { get; set; }

        /// <summary>Optional route-specific host link: wired, wireless, or unknown.
        /// A string tolerates future classes and older clients ignore it.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? HostLink { get; set; }
    }

    public static byte[] EncodeRequest(string nonce)
    {
        return Utf8.GetBytes(JsonSerializer.Serialize(new Request { Nonce = nonce }, DiscoveryJsonContext.Default.Request));
    }

    public static byte[] EncodeReply(Reply reply)
    {
        return Utf8.GetBytes(JsonSerializer.Serialize(reply, DiscoveryJsonContext.Default.Reply));
    }

    /// <summary>Best-effort parse of a request datagram; returns null when the
    /// payload is not a well-formed, version-matched request (so the responder
    /// never wastes a reply on junk).</summary>
    public static Request? TryParseRequest(byte[] data, int length)
    {
        if (length <= 0) return null;
        try
        {
            var req = JsonSerializer.Deserialize(
                Utf8.GetString(data, 0, length), DiscoveryJsonContext.Default.Request);
            if (req is null || req.Magic != Magic || req.V != Version)
                return null;
            return req;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Best-effort parse of a reply datagram; returns null when the
    /// payload is not a well-formed reply (so the searcher ignores junk).</summary>
    public static Reply? TryParseReply(byte[] data, int length)
    {
        if (length <= 0) return null;
        try
        {
            var rep = JsonSerializer.Deserialize(
                Utf8.GetString(data, 0, length), DiscoveryJsonContext.Default.Reply);
            if (rep is null || rep.Magic != Magic || rep.V != Version)
                return null;
            return rep;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string NewNonce()
    {
        Span<byte> b = stackalloc byte[8];
        Random.Shared.NextBytes(b);
        return Convert.ToHexString(b);
    }
}

// Replies are small; indentation costs bytes on UDP for no benefit here.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false)]
[JsonSerializable(typeof(DiscoveryProtocol.Request))]
[JsonSerializable(typeof(DiscoveryProtocol.Reply))]
public partial class DiscoveryJsonContext : JsonSerializerContext;
