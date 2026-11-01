using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.StaticCoreText;
using Allegiance.Core.Wire;

// Run the published native server as a separate process, then load the same
// text-only folder through the ordinary client resolver and join over TCP.
if (args.Length != 3)
{
    Console.Error.WriteLine("usage: p908-text-core-proof <native-server> <artwork> <scratch-directory>");
    return 2;
}
string nativeServer = Path.GetFullPath(args[0]);
string artwork = Path.GetFullPath(args[1]);
string scratch = Path.GetFullPath(args[2]);
Directory.CreateDirectory(scratch);
string text = Path.Combine(scratch, "text");
StaticCoreRoundtrip.Export(IgcReader.ParseFile(Path.Combine(artwork, "PCore001.igc")), text);
File.Copy(Path.Combine(artwork, "XXV_simple.igc"), Path.Combine(scratch, "XXV_simple.igc"), true);
// Bind to a free loopback port briefly, so no fixed service or remote host is needed.
using var reservation = new TcpListener(IPAddress.Loopback, 0);
reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
string config = Path.Combine(scratch, "server.json");
File.WriteAllText(config, """
    {"artPath":".","staticCore":"text.igc","bindAddress":"127.0.0.1","port":PORT,
     "broadcast":{"immutableSnapshot":true,"encodeOnce":true,"parallelBuild":true,"shipUpdateModel":"native"},
     "transport":{"mode":"pool","protocol":"tcp"},"sim":{"nativeGotoPlan":true},
     "games":[{"name":"P908 AOT","map":"XXV_simple.igc","teams":2,"civIds":[1,3],"sideNames":["Alpha","Bravo"]}]}
    """.Replace("PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
var start = new ProcessStartInfo(nativeServer)
{
    WorkingDirectory = scratch, RedirectStandardOutput = true, RedirectStandardError = true,
};
start.ArgumentList.Add("--config"); start.ArgumentList.Add(config);
using var server = Process.Start(start) ?? throw new InvalidOperationException("Native server did not start.");
using var ready = new ManualResetEventSlim();
using var log = new StreamWriter(Path.Combine(scratch, "native-server.log")) { AutoFlush = true };
object logGate = new();
void Line(string? line)
{
    if (line is null) return;
    lock (logGate) log.WriteLine(line);
    if (line.Contains("transport listening", StringComparison.Ordinal)) ready.Set();
}
server.OutputDataReceived += (_, e) => Line(e.Data);
server.ErrorDataReceived += (_, e) => Line(e.Data);
server.BeginOutputReadLine(); server.BeginErrorReadLine();
try
{
    if (!ready.Wait(TimeSpan.FromSeconds(30))) throw new InvalidOperationException("Native server did not become ready; inspect native-server.log.");
    Environment.SetEnvironmentVariable(ExternalPaths.ArtworkEnvVar, scratch);
    using var client = new ConnectionSession { TransportProtocol = "tcp" };
    client.Connect((ushort)port, "AotTextPilot");
    client.SendLogonRequest(new() { CharacterName = "AotTextPilot", DwCookie = uint.MaxValue,
        FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1 });
    Wait(() => client.Ack is not null, "LOGONACK");
    if (!client.Ack!.FValidated) throw new InvalidOperationException("Native server refused logon.");
    Wait(() => client.World.SideRoster is not null, "side roster");
    client.SendPositionReq(0);
    Wait(() => client.World.JoinedShipId.HasValue || client.World.JoinError is not null, "JOINED_MISSION");
    if (client.World.JoinError is { } error) throw new InvalidOperationException(error);
    Wait(() => client.World.MyShipId.HasValue, "SET_CLUSTER");
    if (client.World.Mission!.StaticCoreVersion != 1337 || client.World.MissionCore!.Version != 1337)
        throw new InvalidOperationException("Server/client did not load the text version.");
    if (File.Exists(Path.Combine(scratch, "text.igc"))) throw new InvalidOperationException("Binary unexpectedly present.");
    Console.WriteLine($"PASS: native AOT server + production TCP client joined text-only core v1337; " +
        $"{client.World.MissionCore.HullTypes.Count} hulls, {client.World.MissionCore.Civilizations.Count} civilizations, " +
        $"{client.World.Clusters.Count} clusters; ship={client.World.MyShipId}.");
    return 0;
}
finally
{
    // Terminate only the child process this proof owns, then drain its output.
    if (!server.HasExited) server.Kill();
    server.WaitForExit();
}

static void Wait(Func<bool> condition, string state)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed.TotalSeconds < 30)
    {
        if (condition()) return;
        Thread.Sleep(10);
    }
    throw new InvalidOperationException($"Timed out waiting for {state}.");
}
