using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.ClientCore.Replay;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Transport;
using SimVec = Allegiance.Core.Collision.Vector3;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer.Benchmark;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace HudProof;

// Exercise the shipping replay, capture and renderer in a fresh DesktopGL process.
// A second process gets identical game-thread history and a receive-side rotation
// immediately after capture. The test compares the output of the two renderers.
internal static class RenderSnapshotProof
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    internal static void Run(string skin, string mode, string clientBin)
    {
        var replayPath = Path.GetFullPath("data/benchmarks/battle.alr");
        var command = BenchmarkCommand.Parse(["--benchmark", $"replay:{replayPath}"]);
        using var game = new Game1(new Game1.LaunchOptions { Intro = true, WindowWidth = 960, WindowHeight = 540, Benchmark = command });
        game.Content.RootDirectory = Path.Combine(clientBin, "Content");
        for (int i = 0; i < 4; i++) game.RunOneFrame();

        T Field<T>(string name) => (T)typeof(Game1).GetField(name, Instance)!.GetValue(game)!;
        object? Call(string name, params object[] args) => typeof(Game1).GetMethod(name, Instance)!.Invoke(game, args);
        var session = Field<ConnectionSession>("_session");
        var replay = Field<BattleReplay>("_benchReplay");
        var skins = Field<HudSkinSwitcher>("_hudSkins");
        skins.Request(skin);
        skins.ApplyPending();
        var observe = new Observer(skins.Active.Renderer);
        typeof(HudSkinSwitcher).GetProperty("Active")!.SetValue(skins, skins.Active with { Renderer = observe });
        while (replay.Frame < 6500) replay.Advance(session);
        for (int i = 0; i < 4; i++) game.RunOneFrame();

        if (mode == "transitions")
        {
            var world = session.World;
            var original = world.Ships[world.MyShipId!.Value];
            var set = new SetCluster { SectorId = original.SectorId, Ship = original.Latest! };
            var ship = new ExportShip { ShipId = original.ShipId, SideId = 0, HullId = original.HullId, CharacterName = "Pilot" };
            void Seat()
            {
                world.Apply(set);
                world.Apply(ship);
                world.Apply(new JoinSide { SideId = 0, ShipId = ship.ShipId });
            }
            var time = new GameTime(TimeSpan.FromSeconds(110), TimeSpan.FromSeconds(1d / 60));
            world.LifepodHullResolver = _ => (short)(ship.HullId + 1);
            for (int cycle = 0; cycle < 3; cycle++)
                foreach (var transition in new[] { "eject", "side", "warp", "rotate", "resume" })
                {
                    lock (session.Gate) Seat();
                    Call("GatherRenderPoses", time, 0);
                    if (!(bool)Call("CaptureRenderSnapshot", time)!)
                        throw new InvalidOperationException("Expected a seated frame before " + transition);
                    Task.Run(() =>
                    {
                        lock (session.Gate)
                        {
                            switch (transition)
                            {
                                case "eject":
                                    world.Apply(new KillShip { ShipId = ship.ShipId, BDeathCredit = false });
                                    world.Apply(Eject.ForSurvivor(ship.ShipId, SimVec.Zero, SimVec.Zero, new(0, 0, -1)));
                                    break;
                                case "side": world.Apply(new JoinSide { ShipId = ship.ShipId, SideId = 1 }); break;
                                case "warp": world.Apply(set); break;
                                case "rotate": world.BeginNextMission(); Seat(); break;
                                case "resume": world.ResetPreservingMissionIdentity(); Seat(); break;
                            }
                        }
                    }).GetAwaiter().GetResult();
                    if ((bool)Call("CaptureRenderSnapshot", time)!)
                        throw new InvalidOperationException("Stale scene admitted after " + transition);
                    Call("Draw", time);
                    if (observe.Last!.Context.Contacts.Count != 0 || Field<Texture2D?>("_hudTargetModelPreview") != null)
                        throw new InvalidOperationException("Stale HUD after " + transition);
                }
            Console.WriteLine("PROOF transitions=15");
            return;
        }
        var transport = new CommandStream();
        var wire = typeof(ConnectionSession).GetField("_transport", Instance)!;
        wire.SetValue(session, transport);
        short own = session.World.MyShipId!.Value;
        session.SendWingId(own, 2);
        session.SendOrderChange(own, 1, 2);
        session.SendRipcordRequest(-1);
        if (transport.Packets.Count != 3) throw new InvalidOperationException("Expected three ordered datagrams.");

        if (mode == "native")
            game.RunOneFrame();
        else
        {
            var time = new GameTime(TimeSpan.FromSeconds(replay.Frame / 60d), TimeSpan.FromSeconds(1d / 60));
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            bool scene = (bool)Call("CaptureRenderSnapshot", time)!;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start) > TimeSpan.FromMilliseconds(250))
                throw new InvalidOperationException("Warm snapshot capture exceeded 250 ms.");
            if (!scene) throw new InvalidOperationException("No seated scene captured.");
            using var entered = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            // Keep a slow receiver in the gate throughout projection, art and drawing.
            // It times out and releases if drawing tries to re-enter, avoiding a deadlock.
            var seated = session.World.Ships[own];
            var replacement = new SetCluster { SectorId = seated.SectorId, Ship = seated.Latest! };
            var identity = new ExportShip { ShipId = own, HullId = seated.HullId, SideId = seated.SideId, CharacterName = "Next mission" };
            var receiver = Task.Run(() =>
            {
                lock (session.Gate)
                {
                    if (mode == "rotate")
                    {
                        session.World.BeginNextMission();
                        session.World.Apply(replacement);
                        session.World.Apply(identity);
                    }
                    entered.Set();
                    return finished.Wait(TimeSpan.FromSeconds(2));
                }
            });
            entered.Wait();
            try
            {
                Call("ResolvePoseArt");
                game.GraphicsDevice.Clear(Color.Black);
                Call("DrawPreparedFrame", time, scene, false);
            }
            finally { finished.Set(); }
            if (!receiver.GetAwaiter().GetResult()) throw new InvalidOperationException("Prepared drawing waited for the receive gate.");
        }

        wire.SetValue(session, null);
        var pixels = new byte[960 * 540 * 4];
        game.GraphicsDevice.GetBackBufferData(pixels);
        var model = observe.Last ?? throw new InvalidOperationException("Missing shared HUD model");
        if (model.Context.Contacts.Count == 0) throw new InvalidOperationException("Empty radar fixture");
        Console.WriteLine($"PROOF pixels={Hash(pixels)} hud={Hash(JsonSerializer.SerializeToUtf8Bytes(model))} commands={string.Join('.', transport.Packets)}");
        if (mode == "rotate" && (bool)Call("CaptureRenderSnapshot", new GameTime())!)
            throw new InvalidOperationException("A replacement mission reused the old poses.");
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private sealed class Observer(IHudSkinRenderer inner) : IHudSkinRenderer
    {
        internal HudModel? Last;
        public string Id => inner.Id;
        public IReadOnlySet<string>? Coverage => inner.Coverage;
        public void Draw(HudModel model, in HudDrawFrame frame) { Last = model; inner.Draw(model, frame); }
        public void DrawConsole(HudModel model) => inner.DrawConsole(model);
        public void DrawCommand(HudModel model) => inner.DrawCommand(model);
        public void SetRadarIcons(RadarIconTextures? icons) => inner.SetRadarIcons(icons);
        public Rectangle DrawOverlayText(string text, Vector2 pos, Color color) => inner.DrawOverlayText(text, pos, color);
        public void Dispose() => inner.Dispose();
    }

    private sealed class CommandStream : ITransport
    {
        internal readonly List<string> Packets = [];
        public bool IsConnected => true;
        public ConnectionId LocalId => default;
        public ushort BoundPort => 0;
        public void Host(ushort port) => throw new NotSupportedException();
        public void Join(string address, ushort port, string name) => throw new NotSupportedException();
        public void Shutdown() { }
        public void Send(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed) => Packets.Add(Convert.ToHexString(data));
        public void SendCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int key) => Send(target, data, false);
        public void SendGuaranteedCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int key) => Send(target, data, true);
    }
}
