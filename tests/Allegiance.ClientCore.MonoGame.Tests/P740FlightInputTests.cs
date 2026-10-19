using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("LiveNetwork")]
public sealed class P740FlightInputTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(Game1 game, string field, object value) => typeof(Game1).GetField(field, Private)!.SetValue(game, value);
    private static T Get<T>(Game1 game, string field) => (T)typeof(Game1).GetField(field, Private)!.GetValue(game)!;
    private static void Call(Game1 game, string method, params object[] args) => typeof(Game1).GetMethod(method, Private)!.Invoke(game, args);
    private static Game1 Headless(ConnectionSession session)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1)); GC.SuppressFinalize(game);
        Set(game, "_session", session); Set(game, "_consolePressEdges", new ActionPressEdges());
        Set(game, "_playerPingsPressEdges", new ActionPressEdges());
        var field = typeof(Game1).GetField("_state", Private)!;
        field.SetValue(game, Enum.Parse(field.FieldType, "Flying"));
        Set(game, "_equipmentSwapInput", new EquipmentSwapInput());
        Set(game, "_flightPartHeld", new HashSet<string>());
        Set(game, "_targetPressEdges", new ActionPressEdges());
        Set(game, "_commandActionInput", new CommandActionInput());
        Set(game, "_joystickFlight", new JoystickFlightController());
        Set(game, "_throttle", 1f);
        return game;
    }

    [Theory]
    [InlineData("Throttle33", -.3f)] [InlineData("Throttle66", .3f)]
    public void Rebound_preset_runs_production_handler_persists_and_suppresses_held_composed_and_primed_edges(string action, float value)
    {
        var map = KeyBindingStore.Instance.Map; string saved = map.ToJson();
        using var session = new ConnectionSession(); var game = Headless(session);
        try
        {
            map.ResetToDefaults();
            var definition = InputActionCatalog.Definitions.Single(a => a.Name == action);
            Assert.Equal(Keys.None, definition.Default.Key); Assert.Null(definition.NativeDefault);
            Assert.Contains("TK_" + action, definition.Description);
            map.SetBinding(action, new(Keys.F18, InputModifiers.Control), force: true);
            var restored = new ActionMap(InputActionCatalog.Definitions); restored.ApplyJson(map.ToJson());
            Assert.Equal(map.BindingOf(action), restored.BindingOf(action));
            var down = new KeyboardState(Keys.F18, Keys.LeftControl);
            var joystick = Get<JoystickFlightController>(game, "_joystickFlight");
            joystick.Update(default);
            var analog = new JoystickAxes(0, 0, 0, .8f);
            Assert.NotNull(joystick.Update(analog).ThrottleOverride);
            Call(game, "ApplyThrottleKeys", down, false); Assert.Equal(value, Get<float>(game, "_throttle"));
            Assert.Null(joystick.Update(analog).ThrottleOverride);
            Set(game, "_throttle", .9f); Call(game, "ApplyThrottleKeys", down, false);
            Assert.Equal(.9f, Get<float>(game, "_throttle"));
            Call(game, "ApplyThrottleKeys", new KeyboardState(), false);
            Call(game, "ApplyThrottleKeys", down, true); Call(game, "ApplyThrottleKeys", down, false);
            Assert.Equal(.9f, Get<float>(game, "_throttle"));
            // Prime the same latch used on flight entry, with unrelated latches
            // initialized by that production method (no graphics device).
            Call(game, "PrimeInFlightKeyLatches", down);
            Call(game, "ApplyThrottleKeys", down, false); Assert.Equal(.9f, Get<float>(game, "_throttle"));
            Call(game, "ApplyThrottleKeys", new KeyboardState(), false);
            Call(game, "ApplyThrottleKeys", down, false); Assert.Equal(value, Get<float>(game, "_throttle"));
            var command = FlightInput.Translate(new FlightInputState { Throttle = Get<float>(game, "_throttle") });
            Assert.Equal(value, command.Controls.Throttle);
        }
        finally { map.ResetToDefaults(); map.ApplyJson(saved); }
    }

    [Fact]
    public void Simultaneous_presets_follow_native_zero_33_66_full_priority()
    {
        var map = KeyBindingStore.Instance.Map; string saved = map.ToJson();
        using var session = new ConnectionSession(); var game = Headless(session);
        try
        {
            map.ResetToDefaults(); map.SetBinding("Throttle33", new(Keys.F17, InputModifiers.None), force: true);
            map.SetBinding("Throttle66", new(Keys.F18, InputModifiers.None), force: true);
            Call(game, "ApplyThrottleKeys", new KeyboardState(Keys.Back, Keys.F17, Keys.F18, Keys.OemBackslash), false);
            Assert.Equal(-1f, Get<float>(game, "_throttle"));
            Call(game, "ApplyThrottleKeys", new KeyboardState(), false);
            Call(game, "ApplyThrottleKeys", new KeyboardState(Keys.F17, Keys.F18, Keys.OemBackslash), false);
            Assert.Equal(-.3f, Get<float>(game, "_throttle"));
        }
        finally { map.ResetToDefaults(); map.ApplyJson(saved); }
    }

    private sealed class Site : ITransportSite
    {
        public readonly TaskCompletionSource<ConnectionId> Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<byte[]> Received = new();
        public void OnPeerJoined(ConnectionId id, string name) => Joined.TrySetResult(id);
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId id, ReadOnlySpan<byte> bytes)
        {
            foreach (var message in FedPacket.Unpack(bytes)) Received.Enqueue(message);
        }
    }

    [Fact]
    public void Player_ping_report_pages_all_players_and_average_without_drawing_beyond_the_panel()
    {
        var map = KeyBindingStore.Instance.Map; string saved = map.ToJson();
        using var session = new ConnectionSession(); var game = Headless(session);
        try
        {
            map.ResetToDefaults(); map.SetBinding("MenuDown", new(Keys.F17, InputModifiers.None), force: true);
            for (short id = 0; id < 40; id++)
                ((ITransportSite)session).OnReceive(ConnectionId.Server,
                    FedPacket.Pack(new PlayerInfo { ShipId = id, CharacterName = $"Pilot{id}" }.Encode()));
            ((ITransportSite)session).OnReceive(ConnectionId.Server, FedPacket.Pack(new PingData { ShipId = -1, Ping = 1 }.Encode()));
            Call(game, "HandlePlayerPingsKey", new KeyboardState(), false);
            var method = typeof(Game1).GetMethod("PlayerPingPage", Private)!;
            var first = (IReadOnlyList<TeamPaneLayout.Line>)method.Invoke(game, null)!;
            Assert.Equal(18, first.Count); Assert.Contains("(1/3)", first[0].Text);
            for (int i = 0; i < 2; i++)
            {
                Call(game, "UpdateMainMenu", new KeyboardState(Keys.F17));
                Call(game, "HandlePlayerPingsKey", new KeyboardState(Keys.F17), false);
                Call(game, "UpdateMainMenu", new KeyboardState());
                Call(game, "HandlePlayerPingsKey", new KeyboardState(), false);
            }
            var last = (IReadOnlyList<TeamPaneLayout.Line>)method.Invoke(game, null)!;
            Assert.Contains("(3/3)", last[0].Text); Assert.Contains(last, line => line.Text.StartsWith("Average:"));
            Assert.InRange(last.Count, 3, 18);
        }
        finally { map.ResetToDefaults(); map.ApplyJson(saved); }
    }

    [Fact]
    public async Task Player_pings_input_sends_golden_request_and_opens_only_on_received_marker()
    {
        var map = KeyBindingStore.Instance.Map; string saved = map.ToJson();
        var site = new Site(); var host = new TcpTransport(site); using var session = new ConnectionSession();
        try
        {
            host.Host(0); session.Connect(host.BoundPort, "P740-input");
            var peer = await site.Joined.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var game = Headless(session); map.ResetToDefaults();
            map.SetBinding("PlayerPings", new(Keys.F18, InputModifiers.None), force: true);
            Call(game, "HandlePlayerPingsKey", new KeyboardState(Keys.F18), true);
            Call(game, "HandlePlayerPingsKey", new KeyboardState(Keys.F18), false);
            Assert.DoesNotContain(site.Received, b => FedMessageHeader.Read(b).Fmid == 195);
            Call(game, "HandlePlayerPingsKey", new KeyboardState(), false);
            Call(game, "HandlePlayerPingsKey", new KeyboardState(Keys.F18), false);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!site.Received.Any(b => FedMessageHeader.Read(b).Fmid == 195) && DateTime.UtcNow < deadline) await Task.Delay(5);
            Assert.Equal(Convert.FromHexString("0400C300"), Assert.Single(site.Received, b => FedMessageHeader.Read(b).Fmid == 195));
            Assert.False(Get<bool>(game, "_playerPingsVisible"));
            host.Send(peer, FedPacket.Pack(Convert.FromHexString("0A00C200FFFF01000000")), true);
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (session.Gate) if (session.World.PlayerPingsRevision == 1) break;
                await Task.Delay(5);
            }
            Call(game, "HandlePlayerPingsKey", new KeyboardState(), false);
            Assert.True(Get<bool>(game, "_playerPingsVisible"));
            Call(game, "HandlePlayerPingsKey", new KeyboardState(Keys.F18), false);
            Assert.False(Get<bool>(game, "_playerPingsVisible"));
            Assert.Single(site.Received, b => FedMessageHeader.Read(b).Fmid == 195);
        }
        finally { host.Shutdown(); map.ResetToDefaults(); map.ApplyJson(saved); }
    }
}
