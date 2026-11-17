using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Allegiance.Core.Identity;
using Allegiance.Core.Wire;

// Minimal proof client only. No production ITransport/factory/browser join path.
if (args.Length == 2 && args[0] == "--embedded")
{
    using var embedded = new Allegiance.Server.Server { SpawnInitialMinersAtBoot = false };
    embedded.Boot(new Allegiance.Server.ServerConfig
    {
        ArtPath = Path.GetFullPath(args[1]), StaticCore = "PCore001.igc", Port = 0, BindAddress = "127.0.0.1",
        Discovery = new() { Enabled = false },
        Games = [new() { Name = "P1048 embedded runtime proof", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], AutoStart = false }],
    });
    Console.WriteLine("EMBEDDED_RUNTIME_WITHOUT_ASPNET_OK");
    return;
}
if (args.Length != 3) throw new ArgumentException("Usage: p1048-ws-proof <published-server-binary> <artwork-root> <scratch-output-directory> OR --embedded <artwork-root>");
string binary = Path.GetFullPath(args[0]), art = Path.GetFullPath(args[1]), scratch = Path.GetFullPath(args[2]);
Directory.CreateDirectory(scratch);
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
string publicKey = Path.Combine(scratch, "public.pem"), config = Path.Combine(scratch, "server.json");
await File.WriteAllTextAsync(publicKey, key.ExportSubjectPublicKeyInfoPem());
await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
{
    artPath = art, staticCore = "PCore001.igc", port = 0, bindAddress = "127.0.0.1", updatesPerSecond = 20,
    discovery = new { enabled = false },
    webSocket = new { enabled = true, port = 0, bindAddress = "127.0.0.1", path = "/game", allowedOrigins = new[] { "http://localhost" }, allowLoopbackDevelopment = true },
    identity = new { issuer = "p1048-proof", audience = "game", publicKeyFile = publicKey, allowAnonymous = false },
    games = new[] { new { name = "P1048 AOT proof", map = "XXV_simple.igc", teams = 2, civIds = new[] { 1, 3 }, sideNames = new[] { "Alpha", "Bravo" }, autoStart = false } },
}));
var portReady = new TaskCompletionSource<ushort>(TaskCreationOptions.RunContinuationsAsynchronously);
var log = new List<string>(); var gate = new object();
var start = new ProcessStartInfo(binary) { WorkingDirectory = scratch, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
start.ArgumentList.Add("--config"); start.ArgumentList.Add(config);
// Keep update/profile state in task scratch. The config has no lobby/update URL.
start.Environment["XDG_CONFIG_HOME"] = scratch;
using var process = new Process { StartInfo = start };
void Record(object _, DataReceivedEventArgs e)
{
    if (e.Data is not { } line) return;
    lock (gate) { if (log.Count < 1000) log.Add(line); }
    const string prefix = "[boot] WebSocket transport listening on ";
    if (line.StartsWith(prefix, StringComparison.Ordinal) && ushort.TryParse(line[prefix.Length..].Split('/')[0], out ushort port)) portReady.TrySetResult(port);
}
process.OutputDataReceived += Record; process.ErrorDataReceived += Record;
process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    ushort port = await portReady.Task.WaitAsync(deadline.Token);
    string account = Guid.NewGuid().ToString("N");
    string token = JoinTokens.Issue(key, "p1048-proof", "game", account, "AotPilot", DateTimeOffset.UtcNow);
    using var peer = await Connect(port, deadline.Token);
    await Send(peer, new LogonRequest { CharacterName = "UntrustedName", ZoneTicket = Encoding.UTF8.GetBytes(token),
        FedsrvVer = LogonRequest.MsgVer, DwCookie = uint.MaxValue, CrcFileList = -1, Time = 1 }, deadline.Token);
    var ack = LogonAck.Decode(await ReadUntil(peer, LogonAck.Fmid, deadline.Token));
    if (!ack.FValidated || ack.CharNameOrFailureReason != "AotPilot") throw new InvalidOperationException("AOT canonical ticket validation failed");
    await Send(peer, new PositionReq { SideId = 0 }, deadline.Token);
    var joined = JoinedMission.Decode(await ReadUntil(peer, JoinedMission.Fmid, deadline.Token));
    if (joined.ShipId != ack.ShipID) throw new InvalidOperationException("AOT side selection did not retain the authenticated ship");
    await Send(peer, new Ping { TimeClient = 1048 }, deadline.Token);
    var ping = Ping.Decode(await ReadUntil(peer, Ping.Fmid, deadline.Token));
    if (ping.TimeClient != 1048) throw new InvalidOperationException("AOT ping routing failed");
    using var replay = await Connect(port, deadline.Token);
    await Send(replay, new LogonRequest { CharacterName = "Replay", ZoneTicket = Encoding.UTF8.GetBytes(token),
        FedsrvVer = LogonRequest.MsgVer, DwCookie = uint.MaxValue, CrcFileList = -1, Time = 1 }, deadline.Token);
    var refused = LogonAck.Decode(await ReadUntil(replay, LogonAck.Fmid, deadline.Token));
    if (refused.FValidated || refused.CharNameOrFailureReason?.Contains("already used", StringComparison.Ordinal) != true)
        throw new InvalidOperationException("AOT replay protection failed");
    var result = new { passed = true, nativeAot = true, canonicalLogon = ack.CharNameOrFailureReason, sideSelect = true,
        pingEcho = true, replayRefused = true, listenerSubprotocol = peer.SubProtocol, shipId = joined.ShipId };
    string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(Path.Combine(scratch, "result.json"), json);
    Console.WriteLine(json);
}
finally
{
    if (!process.HasExited)
    {
        // Graceful SIGTERM to this exact child PID; the server handles it and
        // drains listener tasks. Fall back to killing only this child tree.
        if (OperatingSystem.IsLinux())
        {
            using var signal = Process.Start(new ProcessStartInfo("kill") { ArgumentList = { "-TERM", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
            if (signal is not null) await signal.WaitForExitAsync();
        }
        else process.Kill(entireProcessTree: true);
        using var stop = new CancellationTokenSource(10_000);
        try { await process.WaitForExitAsync(stop.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    }
    process.WaitForExit();
    lock (gate) File.WriteAllLines(Path.Combine(scratch, "server.log"), log);
}

static async Task<ClientWebSocket> Connect(ushort port, CancellationToken token)
{
    var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Origin", "http://localhost");
    socket.Options.AddSubProtocol("allegiance-fed-v1");
    try { await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/game"), token); return socket; }
    catch { socket.Dispose(); throw; }
}
static Task Send(ClientWebSocket socket, IFedMessage message, CancellationToken token) =>
    socket.SendAsync(FedPacket.Pack([message.Encode()]), WebSocketMessageType.Binary, true, token);
static async Task<byte[]> ReadUntil(ClientWebSocket socket, ushort fmid, CancellationToken token)
{
    byte[] buffer = new byte[8192];
    while (true)
    {
        using var packet = new MemoryStream();
        while (true)
        {
            var part = await socket.ReceiveAsync(buffer, token);
            if (part.MessageType != WebSocketMessageType.Binary) throw new InvalidOperationException("Non-binary AOT response");
            packet.Write(buffer, 0, part.Count);
            if (packet.Length > 16 * 1024 * 1024) throw new InvalidOperationException("Oversize AOT response");
            if (part.EndOfMessage) break;
        }
        foreach (byte[] message in FedPacket.Unpack(packet.ToArray()))
            if (FedMessageHeader.Read(message).Fmid == fmid) return message;
    }
}
