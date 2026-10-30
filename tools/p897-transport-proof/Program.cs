using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Discovery;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Transport;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Srv = Allegiance.Server.Server;

// Local-only real Game1 capture: production discovery, policy, handshake and Settings.
// Usage: dotnet run --project tools/p897-transport-proof -- Classic output.png
if (args.Contains("--detect"))
{
    Console.WriteLine($"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; os={System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        Console.WriteLine($"adapter={adapter.Name}; dotnetType={adapter.NetworkInterfaceType}; wirelessSysfs={Directory.Exists($"/sys/class/net/{adapter.Name}/wireless")}; physicalSysfs={Directory.Exists($"/sys/class/net/{adapter.Name}/device")}");
    Console.WriteLine($"loopback={NetworkLinkDetector.Detect(IPAddress.Loopback, 6073)}");
    // The optional route argument is used locally; addresses are not printed.
    if (args.Length > 1) Console.WriteLine($"selected-route={NetworkLinkDetector.Detect(args[1], 6073)}");
    return;
}
var style = Enum.Parse<HudStyle>(args[0]);
string output = Path.GetFullPath(args[1]);
using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    Transport = new TransportConfig { Protocol = "udp", Mode = "pool" },
    Discovery = new DiscoveryConfig { Enabled = false },
    Games = [new GameConfig { Name = "P897 local proof", Map = "XXV_simple.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
});
// A separate ephemeral discovery responder avoids fixed ports and forwards the
// real server's bound UDP port from its transport.
const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
var host = typeof(Srv).GetField("_host", Hidden)!.GetValue(server)!;
var dual = (DualTransport)host.GetType().GetProperty("Transport")!.GetValue(host)!;
using var discovery = new DiscoveryResponder(() => new("P897 local proof", "", 0, 8, "1", server.BoundPort, dual.UdpPort), 0, multicastGroup: "");
discovery.Start();
using var engine = new DiscoveryEngine();
var found = engine.ProbeTargetAsync(new("127.0.0.1", discovery.BoundPort, DiscoverySource.Lan))
    ?? throw new InvalidOperationException("local discovery failed");
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(10); } });
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        Intro = true, PlayerName = "P897 proof",
        WindowWidth = 1280, WindowHeight = 720,
    });
    string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
    game.Content.RootDirectory = content;
    ((List<DiscoveredServer>)typeof(Game1).GetField("_introFound", Hidden)!.GetValue(game)!).Add(found);
    var settings = (HudSettings)typeof(Game1).GetField("_hudSettings", Hidden)!.GetValue(game)!;
    settings.Style = style; settings.Transport = ClientTransportSetting.Auto;
    game.Components.Add(new CaptureSettings(game, output, found));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

sealed class CaptureSettings(Game1 game, string output, DiscoveredServer found) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private bool _armed;
    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 40) throw new TimeoutException("P897 capture timed out");
        if (_armed || _watch.Elapsed.TotalSeconds < 3) return;
        void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
        typeof(Game1).GetMethod("BeginConnect", Hidden)!.Invoke(game, [found.Address, (ushort)found.Port, uint.MaxValue, found]);
        Set("_graphicsSettingsVisible", true); Set("_graphicsRow", 21);
        // EndDraw captures after the real settings overlay is drawn.
        Set("_budgetCapture", output); Set("_budgetStart", Stopwatch.GetTimestamp()); Set("_budgetDuration", 2d);
        _armed = true;
    }
}
