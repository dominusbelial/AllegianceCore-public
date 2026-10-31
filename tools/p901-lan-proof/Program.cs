using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Discovery;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Wire;
using Allegiance.Core.Sim;
using Allegiance.Core;
using Microsoft.Xna.Framework;

if (args[0] is "capture" or "capture-live")
{
    using var game = new Game1(new Game1.LaunchOptions { Intro = true, PlayerName = "LAN pilot", WindowWidth = 1280, WindowHeight = 720 });
    game.Content.RootDirectory = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
    game.Components.Add(new Capture(game, Enum.Parse<HudStyle>(args[1]), Path.GetFullPath(args[2]), args[0] == "capture-live"));
    game.Run();
    return;
}
// A separate client PROCESS, ordinary wire packets, no access to embedded server state.
string address = args[1];
ushort port = ushort.Parse(args[2]);
if (args[3] == "discovery")
{
    using var discovery = new DiscoveryEngine(3000);
    var found = discovery.ProbeTargetAsync(new(address, port + 1, DiscoverySource.Lan))
        ?? throw new IOException("LAN host was not discovered");
    port = checked((ushort)(args[4] == "udp" ? found.UdpPort!.Value : found.Port));
    address = found.Address;
    Console.WriteLine("DISCOVERED " + found.Name);
}
using var session = new ConnectionSession { TransportProtocol = args[4] };
string name = args[5];
session.Connect(port, name, address);
if (session.ActiveTransportProtocol != args[4]) throw new IOException("Requested protocol fell back: " + session.ActiveTransportProtocol);
session.SendLogonRequest(new LogonRequest
{
    CharacterName = name, FedsrvVer = LogonRequest.MsgVer, DwCookie = uint.MaxValue,
    MissionPassword = args.Length > 7 ? args[7] : "", CDKey = "ABCDEFGHIJKL", CrcFileList = -1,
    Time = 1, SteamAuthTicket = new byte[16]
    // Deliberately no optional build/identity extensions: older port clients join too.
});
if (!session.WaitForAck(15000).FValidated) throw new IOException("Guest logon refused");
session.SendPositionReq(short.Parse(args[6]));
var stdin = Task.Run(Console.ReadLine);
var clock = Stopwatch.StartNew();
double nextLaunch = 0;
bool overSeen = false;
Allegiance.Core.Collision.Vector3? initialPosition = null;
double distance = 0;
while (clock.Elapsed.TotalSeconds < 300)
{
    if (session.Lost)
    {
        lock (session.Gate) Console.WriteLine("LOST " + session.World.OwnMissionDeparture?.Reason);
        return;
    }
    if (stdin.IsCompleted)
    {
        string? command = await stdin;
        if (command is null or "quit") { session.SendLogoff(); return; }
        if (command.StartsWith("finale ", StringComparison.Ordinal))
            session.SendShowcaseFinale(Guid.Parse(command[7..]), session.World.MySideId ?? -1);
        if (command == "cheat") { session.SendChat("timescale 0.1"); session.SendChat("money 1000000"); }
        if (command == "status")
        {
            lock (session.Gate) Console.WriteLine("STATUS " + JsonSerializer.Serialize(new
            {
                side = session.World.MySideId, ship = session.World.MyShipId,
                stage = session.World.Mission?.Stage, generation = session.World.MissionGeneration,
                money = session.World.MyMoney, lost = session.Lost, protocol = session.ActiveTransportProtocol,
                refusal = session.World.MyPositionRequestResult?.Reason.ToString(),
                over = session.World.GameOverResult is not null, overSeen, distance,
                flying = session.World.MyShipId is { } id && session.World.Ships.TryGetValue(id, out var s) && s.StationId < 0
            }));
        }
        stdin = Task.Run(Console.ReadLine);
    }
    lock (session.Gate)
    {
        overSeen |= session.World.GameOverResult is not null;
        if (session.World.MyShipId is { } id && session.World.Ships.TryGetValue(id, out var ship))
        {
            session.SendPlayerReady(true, id);
            if (ship.StationId < 0 && ship.Latest is { } pose)
            {
                initialPosition ??= pose.Position;
                distance = Math.Max(distance, (pose.Position - initialPosition.Value).Length());
                session.CommandControls(new Allegiance.Core.Sim.ControlData(.02f, 0, 0, 1), ShipControlState.AllWeapons);
            }
            if (ship.StationId >= 0 && clock.Elapsed.TotalSeconds >= nextLaunch
                && session.World.Mission?.Stage == MissionStageMessage.StageStarted)
            {
                session.SendBuyLoadout(ship.HullId, true, ship.Loadout);
                nextLaunch = clock.Elapsed.TotalSeconds + 2;
            }
        }
    }
    await Task.Delay(100);
}
throw new TimeoutException("Guest proof exceeded its time bound");

sealed class Capture(Game1 game, HudStyle style, string output, bool live) : GameComponent(game)
{
    const BindingFlags H = BindingFlags.Instance | BindingFlags.NonPublic;
    readonly Stopwatch watch = Stopwatch.StartNew();
    bool armed, started;
    public override void Update(GameTime time)
    {
        if (watch.Elapsed.TotalSeconds < 3) return;
        void Set(string name, object value) => typeof(Game1).GetField(name, H)!.SetValue(game, value);
        var view = typeof(Game1).GetNestedType("IntroView", BindingFlags.NonPublic)!;
        if (watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("LAN window capture timed out");
        if (!live || !started)
        {
            Set("_introView", Enum.Parse(view, "HostLan"));
            var state = typeof(Game1).GetField("_state", H)!.FieldType;
            Set("_state", Enum.Parse(state, "Intro"));
        }
        if (live)
        {
            if (!started)
            {
                var setup = (Allegiance.SinglePlayer.HostLanScreenModel)typeof(Game1).GetField("_hostLanScreen", H)!.GetValue(game)!;
                setup.Move(Allegiance.SinglePlayer.HostLanScreenModel.StartRow);
                Set("_iEnter", false);
                typeof(Game1).GetMethod("UpdateHostLan", H)!.Invoke(game,
                    [new Microsoft.Xna.Framework.Input.KeyboardState(Microsoft.Xna.Framework.Input.Keys.Enter), false, false, true, false]);
                started = true;
                return;
            }
            if (typeof(Game1).GetField("_skirmish", H)!.GetValue(game) is null) return;
            if (typeof(Game1).GetField("_state", H)!.GetValue(game)!.ToString() != "Flying") return;
        }
        if (armed) return;
        ((HudSettings)typeof(Game1).GetField("_hudSettings", H)!.GetValue(game)!).Style = style;
        Set("_budgetCapture", output); Set("_budgetStart", Stopwatch.GetTimestamp()); Set("_budgetDuration", 2d);
        armed = true;
    }
}
