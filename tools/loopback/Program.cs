using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

// Two-process loopback proof over a real socket boundary.
//
//   loopback host                # binds an ephemeral port, prints it, serves one
//                                # client handshake, prints both halves
//   loopback client <port> [name]# joins 127.0.0.1:<port>, sends FM_C_LOGONREQ,
//                                # prints the received FM_S_LOGONACK, disconnects
//
// Run `tools/loopback/run.sh` to launch host + client as two separate OS
// processes and show both transcripts side by side.

switch (args.FirstOrDefault())
{
    case "host":
        return await RunHost();
    case "client":
        return await RunClient(args.Skip(1).ToArray());
    default:
        Console.Error.WriteLine("usage:");
        Console.Error.WriteLine("  loopback host            # bind ephemeral port, print it, serve one client");
        Console.Error.WriteLine("  loopback client <port> [playerName]");
        return 1;
}

async Task<int> RunHost()
{
    static string BitString(byte[]? b) => b is null ? "(none)" : Convert.ToHexString(b);
    var tcs = new TaskCompletionSource();
    ITransport? host = null;

    ITransportSite site = new Site
    {
        OnPeerJoined = (id, name) => Console.WriteLine($"[host] peer {id} joined as '{name}'"),
        OnPeerLeft = id => { Console.WriteLine($"[host] peer {id} left — shutting down"); host!.Shutdown(); tcs.SetResult(); },
        OnReceive = (from, data) =>
        {
            var messages = FedPacket.Unpack(data.ToArray());      // validates CRC
            var req = LogonRequest.Decode(messages.Single());         // FM_C_LOGONREQ
            Console.WriteLine($"[host] LOGONREQ from {from}:");
            Console.WriteLine($"    character='{req.CharacterName}'  zone={BitString(req.ZoneTicket)}  cdkey='{req.CDKey}'");
            Console.WriteLine($"    password='{req.MissionPassword}'  fedsrvVer={req.FedsrvVer} ({(req.VersionMatches ? "ok" : "BAD")})");
            Console.WriteLine($"    time={req.Time}  cookie=0x{req.DwCookie:X8}  crcFileList={req.CrcFileList}");
            Console.WriteLine($"    steamID={req.SteamID}  ticketLen={req.SteamAuthTicketLength}  drmHash='{req.DrmHash}'");

            var ack = new LogonAck
            {
                CharNameOrFailureReason = $"Welcome {req.CharacterName}",
                TimeServer = req.Time + 1,
                ShipID = 712,
                FValidated = true,
                FRetry = false,
                Cookie = (byte)(req.DwCookie & 0xFF),
            };
            host!.Send(from, FedPacket.Pack(new[] { ack.Encode() }), guaranteed: true);
            Console.WriteLine($"[host] replied FM_S_LOGONACK (shipID={ack.ShipID}, validated={ack.FValidated})");
        },
    };

    host = new TcpTransport(site);
    host.Host(0);
    Console.WriteLine($"{host.BoundPort}");   // first line: the port for the script
    await tcs.Task;
    return 0;
}

async Task<int> RunClient(string[] args)
{
    if (args.Length == 0 || !ushort.TryParse(args[0], out var port))
    {
        Console.Error.WriteLine("usage: loopback client <port> [playerName]");
        return 1;
    }
    string name = args.Length > 1 ? args[1] : "BOT-ONE";

    var tcs = new TaskCompletionSource();
    ITransport? client = null;

    ITransportSite site = new Site
    {
        OnReceive = (from, data) =>
        {
            var messages = FedPacket.Unpack(data.ToArray());      // validates CRC
            var ack = LogonAck.Decode(messages.Single());             // FM_S_LOGONACK
            Console.WriteLine($"[client] LOGONACK from {from}: reason='{ack.CharNameOrFailureReason}' "
                + $"timeServer={ack.TimeServer} shipID={ack.ShipID} validated={ack.FValidated} retry={ack.FRetry} cookie=0x{ack.Cookie:X2}");
            client!.Shutdown();
            tcs.SetResult();
        },
    };

    client = new TcpTransport(site);
    client.Join("127.0.0.1", port, name);
    Console.WriteLine($"[client] connected to 127.0.0.1:{port} as '{name}', local id {client.LocalId}");

    var request = new LogonRequest
    {
        CharacterName = name,
        ZoneTicket = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
        CDKey = "AABBCCDDEEFF",
        MissionPassword = "pw9",
        FedsrvVer = LogonRequest.MsgVer,
        Time = 8675309u,
        DwCookie = 0xC0FFEEu,
        CrcFileList = -7,
        SteamAuthTicket = new byte[1024],
        SteamAuthTicketLength = 0,
        SteamID = 772829102919975137ul,
        DrmHash = "loopback",
    };
    client.Send(ConnectionId.Server, FedPacket.Pack(new[] { request.Encode() }), guaranteed: true);
    Console.WriteLine($"[client] sent FM_C_LOGONREQ ({request.CharacterName})");

    await tcs.Task;
    return 0;
}

// Minimal ITransportSite that lets us route individual handlers; unnamed events
// are no-ops so the CLI stays readable. A custom delegate is required because
// ReadOnlySpan<byte> (a ref struct) cannot be a generic type argument.
public delegate void ReceiveHandler(ConnectionId from, ReadOnlySpan<byte> data);

sealed class Site : ITransportSite
{
    public Action<ConnectionId, string>? OnPeerJoined;
    public Action<ConnectionId>? OnPeerLeft;
    public Action OnSessionLost = () => { };   // unused on the loopback path
    public ReceiveHandler? OnReceive;

    void ITransportSite.OnPeerJoined(ConnectionId id, string name) => OnPeerJoined?.Invoke(id, name);
    void ITransportSite.OnPeerLeft(ConnectionId id) => OnPeerLeft?.Invoke(id);
    void ITransportSite.OnSessionLost() => OnSessionLost?.Invoke();
    void ITransportSite.OnReceive(ConnectionId from, ReadOnlySpan<byte> data) => OnReceive?.Invoke(from, data);
}
