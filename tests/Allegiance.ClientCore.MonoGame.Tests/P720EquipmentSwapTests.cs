using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("LiveNetwork")]
public class P720EquipmentSwapTests
{
    public static TheoryData<string, int, int> Commands => new()
    {
        { "SwapWeapon1", 1, 0 }, { "SwapWeapon2", 1, 1 }, { "SwapWeapon3", 1, 2 }, { "SwapWeapon4", 1, 3 },
        { "SwapTurret1", 1, 4 }, { "SwapTurret2", 1, 5 }, { "SwapTurret3", 1, 6 }, { "SwapTurret4", 1, 7 },
        { "SwapMissile", 2, 0 }, { "SwapChaff", 0, 0 }, { "SwapShield", 4, 0 }, { "SwapCloak", 5, 0 },
        { "SwapAfterburner", 7, 0 }, { "SwapMine", 3, 0 },
    };
    private static DataHullTypeIGC Hull => default(DataHullTypeIGC) with
    { HullId = 720, MaxWeapons = 8, MaxFixedWeapons = 4, MaxFuel = 100, CapacityMagazine = 10,
        CapacityChaffLauncher = 10, CapacityDispenser = 10,
        Hardpoints = Enumerable.Range(0, 8).Select(i => new DataHullTypeIGC.Hardpoint(0, 0, "", "", 1, i < 4)).ToArray(),
        PmEquipment = [1, 1, 1, 1, 1, 1, 1, 1] };
    private static DataPartTypeIGC Part(short id, int kind = 1, short mask = 1) => new(0, 0, "", "", "P720", "", 0, [], [], 0, 0, id, -1, mask, "", kind switch
    {
        0 => new DataPartTypeIGC.ChaffLauncher(0), 2 => new DataPartTypeIGC.MissileLauncher(0),
        3 => new DataPartTypeIGC.ProbeLauncher(0), 4 => new DataPartTypeIGC.Shield(0, 0, 0, 0, 0),
        5 => new DataPartTypeIGC.Cloak(0, 0, 0, 0, 0, 0), 6 => new DataPartTypeIGC.Pack(1, 100),
        7 => new DataPartTypeIGC.Afterburner(0, 0, 0, 0, 0, 0),
        _ => new DataPartTypeIGC.Weapon(0, 0, 0, 0, 0, 0, 0, 0, 0)
    });
    private static ExpandedPartData Entry(short id, int mount, short amount = 1) => new(id, amount, unchecked((byte)mount), 240);

    [Theory]
    [MemberData(nameof(Commands))]
    public void Every_action_is_discoverable_persistent_and_edge_triggered(string action, int kind, int mount)
    {
        _ = kind; _ = mount;
        var definition = InputActionCatalog.Definitions.Single(a => a.Name == action);
        Assert.Contains("TK_" + action, definition.Description);
        var map = new ActionMap(InputActionCatalog.Definitions);
        Assert.Equal(Keys.None, map.BindingOf(action).Key);
        map.SetBinding(action, new(Keys.F18, InputModifiers.Control));
        var restored = new ActionMap(InputActionCatalog.Definitions); restored.ApplyJson(map.ToJson());
        var input = new EquipmentSwapInput(); var keys = new KeyboardState(Keys.F18, Keys.LeftControl);
        Assert.Equal(action, input.Step(keys, restored, false));
        Assert.Null(input.Step(keys, restored, false));
        input.Step(default, restored, false);
        Assert.Null(input.Step(keys, restored, true));
        Assert.Null(input.Step(keys, restored, false));
        input.Step(default, restored, false); input.Prime(keys, restored);
        Assert.Null(input.Step(keys, restored, false));
        input.Step(default, restored, false);
        Assert.Equal(action, input.Step(keys, restored, false));
        var native = DefaultInputMap.ParseKeyMap(DefaultInputMap.EmbeddedText).Where(b => b.TkNumber == 130 + Array.IndexOf(EquipmentSwapInput.Actions, action)).ToList();
        if (action.StartsWith("SwapTurret")) Assert.Empty(native);
        else Assert.Equal(definition.NativeDefault, DefaultInputMap.ToChord(Assert.Single(native)));
    }

    [Fact]
    public void Selection_honors_current_slot_masks_different_types_empty_slots_and_full_hold()
    {
        var types = new Dictionary<short, DataPartTypeIGC> { [10] = Part(10), [11] = Part(11), [12] = Part(12, mask: 2), [20] = Part(20, 4) };
        DataPartTypeIGC? Resolve(short id) => types.GetValueOrDefault(id);
        ExpandedPartData[] inventory = [Entry(10, 0), Entry(20, -1), Entry(10, -2), Entry(12, -3), Entry(11, -4)];
        // selected compatible cargo wins; no global "first compatible" shortcut.
        var swap = EquipmentSwapInput.Plan("SwapWeapon1", Hull, inventory, Resolve, -4)!.Value;
        Assert.Equal(-4, swap.Request.MountNew);
        Assert.Equal(-5, swap.SelectedCargoMount); // native next empty slot can unmount
        Assert.Empty(swap.Reloads);
        Assert.Equal(-4, EquipmentSwapInput.Plan("SwapWeapon1", Hull, inventory, Resolve, -1)!.Value.Request.MountNew);
        Assert.Equal(-5, EquipmentSwapInput.Plan("SwapWeapon1", Hull, inventory, Resolve, -5)!.Value.Request.MountNew);
        Assert.Equal(-1, EquipmentSwapInput.Plan("SwapWeapon1", Hull, [Entry(10, 0)], Resolve, -4)!.Value.Request.MountNew);
        Assert.Null(EquipmentSwapInput.Plan("SwapWeapon1", Hull, Enumerable.Range(1, 5).Select(i => Entry(10, -i)).Append(Entry(10, 0)).ToArray(), Resolve, -1));
        Assert.Null(EquipmentSwapInput.Plan("SwapWeapon1", Hull, [], Resolve, -1));
        Assert.Null(EquipmentSwapInput.Plan("unknown", Hull, inventory, Resolve, -1));
        Assert.Null(EquipmentSwapInput.Plan("SwapWeapon4", Hull with { MaxFixedWeapons = 3 }, inventory, Resolve, -1));
        Assert.Null(EquipmentSwapInput.Plan("SwapTurret4", Hull with { MaxWeapons = 7 }, inventory, Resolve, -1));
        // Turret 1 is maxFixed, not hardpoint 0, and uses its own mask.
        var hull = Hull with { Hardpoints = Hull.Hardpoints.Select((p, i) => i == 4 ? p with { PartMask = 2 } : p).ToArray() };
        Assert.Equal(-3, EquipmentSwapInput.Plan("SwapTurret1", hull, inventory, Resolve, -1)!.Value.Request.MountOld);
    }

    [Theory]
    [InlineData("SwapMissile", 2)] [InlineData("SwapChaff", 0)] [InlineData("SwapMine", 3)]
    public void Selected_duplicate_launcher_stack_normalizes_to_first_group_representative(string action, int kind)
    {
        // trekigc.cpp:2603-2655 skips duplicate OT_magazine/OT_dispenser stacks,
        // including chaff (dispenserigc.cpp:45-47, dispenserigc.h:34-37).
        DataPartTypeIGC? Resolve(short id) => Part(id, kind);
        var decision = EquipmentSwapInput.Plan(action, Hull,
            [Entry(10, 0), Entry(11, -1), Entry(11, -3)], Resolve, -3)!.Value;
        Assert.Equal(0, decision.Request.MountOld);
        Assert.Equal(-1, decision.Request.MountNew);
        Assert.Equal(-2, decision.SelectedCargoMount);
        Assert.Empty(decision.Reloads);
    }

    [Theory]
    [InlineData("SwapMissile", 2)] [InlineData("SwapChaff", 0)] [InlineData("SwapMine", 3)]
    public void Empty_launcher_mount_reloads_last_matching_stacks_then_selects_after_consumption(string action, int kind)
    {
        DataPartTypeIGC? Resolve(short id) => Part(id, kind);
        var decision = EquipmentSwapInput.Plan(action, Hull, [Entry(10, -1, 2), Entry(10, -2, 3), Entry(10, -4, 8)], Resolve, -1)!.Value;
        Assert.Equal(-1, decision.Request.MountOld); Assert.Equal(0, decision.Request.MountNew);
        Assert.Equal(new ReloadData[] { new(8, -4) }, decision.Reloads);
        Assert.Equal(-1, decision.SelectedCargoMount); // empty slot wins; remaining stack is the mounted type
        var partial = EquipmentSwapInput.Plan(action, Hull, [Entry(10, -1, 9), Entry(10, -2, 3)], Resolve, -1)!.Value;
        Assert.Equal(new ReloadData[] { new(1, -2) }, partial.Reloads);
    }

    [Fact]
    public void Empty_booster_mount_reloads_only_fuel_last_pack_first_and_leaves_replacement_alone()
    {
        DataPartTypeIGC? Resolve(short id) => Part(id, id >= 20 ? 6 : 7);
        var decision = EquipmentSwapInput.Plan("SwapAfterburner", Hull,
            [Entry(10, -1), Entry(20, -2, 30), Entry(21, -5, 40)], Resolve, -1, 50)!.Value;
        Assert.Equal(new ReloadData[] { new(-1, -5), new(10, -2) }, decision.Reloads);
        Assert.Empty(EquipmentSwapInput.Plan("SwapAfterburner", Hull,
            [Entry(11, 0), Entry(10, -1), Entry(20, -2, 30)], Resolve, -1, 0)!.Value.Reloads);
    }

    [Fact]
    public void Reload_echo_moves_orphan_launchers_and_applies_partial_and_whole_packs_in_wire_order()
    {
        using var session = new ConnectionSession();
        session.World.PartTypeResolver = id => Part(id, id == 20 ? 6 : 2);
        session.World.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(720, 0, default, 0,
            Vector3.Zero, Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        session.World.Apply(new BuyLoadoutAck { HullId = 720, Parts = [Entry(10, 0, 0), Entry(11, -1, 2),
            Entry(11, -3, 8), Entry(20, -2, 40), Entry(20, -5, 10)] });
        var reload = new Reload { ShipID = 720, Entries = [new(-1, -1), new(3, -3), new(10, -2), new(-1, -5)] };
        ((ITransportSite)session).OnReceive(ConnectionId.Server, FedPacket.Pack([reload.Encode()]));
        var ship = session.World.Ships[720];
        Assert.Equal(1, ship.ReloadCount);
        var mounted = Assert.Single(ship.Loadout!);
        Assert.Equal(11, mounted.PartId); Assert.Equal(5, mounted.Amount); Assert.Equal(0, mounted.FractionMounted);
        Assert.Collection(ship.Cargo!.OrderByDescending(p => unchecked((sbyte)p.MountId)),
            p => { Assert.Equal(20, p.PartId); Assert.Equal(30, p.Amount); },
            p => { Assert.Equal(11, p.PartId); Assert.Equal(5, p.Amount); });
        var before = ship.Cargo!.ToArray();
        ((ITransportSite)session).OnReceive(ConnectionId.Server, FedPacket.Pack([new Reload { ShipID = 720,
            Entries = [new(9, -3), new(1, -6), new(-1, -4)] }.Encode()]));
        Assert.Equal(before, ship.Cargo);
        session.World.Apply(new Reload { ShipID = 999, Entries = [new(-1, -1)] });
        Assert.False(session.World.Ships.ContainsKey(999));
    }

    private sealed class WireSite : ITransportSite, IDisposable
    {
        public readonly ConcurrentQueue<byte[]> Packets = new();
        public TcpTransport Host = null!;
        public void Dispose() => Host.Shutdown();
        public void OnPeerJoined(ConnectionId id, string name) { }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> bytes)
        {
            Packets.Enqueue(bytes.ToArray());
            var messages = FedPacket.Unpack(bytes).Select(b => b.ToArray()).ToArray();
            foreach (var message in messages)
            { int offset = message[2] == 129 ? 8 : 4; message[offset] = 0xD0; message[offset + 1] = 0x02; }
            Host.Send(from, FedPacket.Pack(messages), true);
        }
    }
    private static void Wait(Func<bool> done) => Assert.True(SpinWait.SpinUntil(done, 5000), "P720 loopback transition timed out");
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object obj, string field, object value) => obj.GetType().GetField(field, Private)!.SetValue(obj, value);
    private static void Invoke(Game1 game, string method, params object[] args) => typeof(Game1).GetMethod(method, Private)!.Invoke(game, args);

    [Theory]
    [MemberData(nameof(Commands))]
    public void Production_handler_sends_exact_native_bytes_over_tcp_and_applies_ordered_own_echo_once(string action, int kind, int mount)
    {
        using var site = new WireSite(); var host = new TcpTransport(site); site.Host = host; host.Host();
        using var session = new ConnectionSession(); session.Connect(host.BoundPort, "p720-wire");
        DataPartTypeIGC? Resolve(short id) => Part(id, kind);
        session.World.PartTypeResolver = Resolve;
        session.World.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(720, 0, default, 0,
            Vector3.Zero, Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        session.World.Apply(new BuyLoadoutAck { HullId = 720, Parts = [Entry(10, mount), Entry(11, -2)] });
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1)); GC.SuppressFinalize(game);
        Set(game, "_session", session); Set(game, "_shopCatalog", new ShopCatalog([Hull], [Part(10, kind), Part(11, kind)]));
        var stateField = typeof(Game1).GetField("_state", Private)!;
        stateField.SetValue(game, Enum.Parse(stateField.FieldType, "Flying"));
        Set(game, "_selectedCargoMount", -2);
        Invoke(game, "SwapFlightEquipment", action);
        Wait(() => { lock (session.Gate) return session.World.Ships[720].Loadout!.Single().PartId == 11; });
        Assert.True(site.Packets.TryDequeue(out var packet));
        byte[] expected = [10, 0, 34, 0, 0, 0, (byte)kind, 0, (byte)mount, 254];
        Assert.Equal(FedPacket.Pack([expected]), packet);
        lock (session.Gate)
        {
            Assert.Equal(10, session.World.Ships[720].Cargo!.Single().PartId);
            Assert.Equal(0, session.World.Ships[720].Loadout!.Single().FractionMounted);
        }
        Assert.Equal(-1, typeof(Game1).GetField("_selectedCargoMount", Private)!.GetValue(game));
        // A second request must be ordered after the first and swap back once.
        Set(game, "_selectedCargoMount", -2);
        Invoke(game, "SwapFlightEquipment", action);
        Wait(() => { lock (session.Gate) return session.World.Ships[720].Loadout!.Single().PartId == 10; });
        Assert.True(site.Packets.TryDequeue(out packet)); Assert.Equal(FedPacket.Pack([expected]), packet);
        // Guarded contexts produce no request; a subsequent allowed request fences absence.
        session.World.Apply(new ShipStatusMessage { ShipId = 720, HullId = 720, ParentId = 999, StationId = -1, State = ShipStatusMessage.StateTurret });
        Invoke(game, "SwapFlightEquipment", action);
        session.World.Apply(new ShipStatusMessage { ShipId = 720, HullId = 720, ParentId = -1, StationId = 42, State = ShipStatusMessage.StateDocked });
        Invoke(game, "SwapFlightEquipment", action);
        session.World.Apply(new ShipStatusMessage { ShipId = 720, HullId = 720, ParentId = -1, StationId = -1, State = ShipStatusMessage.StateFlying });
        stateField.SetValue(game, Enum.Parse(stateField.FieldType, "ReadyCheck")); Invoke(game, "SwapFlightEquipment", action);
        stateField.SetValue(game, Enum.Parse(stateField.FieldType, "Flying")); Set(game, "_selectedCargoMount", -2);
        Invoke(game, "SwapFlightEquipment", action);
        Wait(() => { lock (session.Gate) return session.World.Ships[720].Loadout!.Single().PartId == 11; });
        Assert.True(site.Packets.TryDequeue(out packet)); Assert.Equal(FedPacket.Pack([expected]), packet);
        Assert.Empty(site.Packets);
    }
    [Theory]
    [InlineData("SwapMissile", 2)] [InlineData("SwapChaff", 0)]
    [InlineData("SwapMine", 3)] [InlineData("SwapAfterburner", 7)]
    public void Empty_mount_sends_swap_before_exact_reload_and_echo_consumes_cargo(string action, int kind)
    {
        using var site = new WireSite(); var host = new TcpTransport(site); site.Host = host; host.Host();
        using var session = new ConnectionSession(); session.Connect(host.BoundPort, "p720-reload");
        DataPartTypeIGC? Resolve(short id) => Part(id, id == 20 ? 6 : kind);
        session.World.PartTypeResolver = Resolve;
        session.World.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(720, 0, default, 0,
            Vector3.Zero, Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, .5f, 1, 0, 1) });
        session.World.Apply(new BuyLoadoutAck { HullId = 720, Parts = kind == 7
            ? [Entry(10, -1), Entry(20, -5, 60)] : [Entry(10, -1, 2), Entry(10, -5, 8)] });
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1)); GC.SuppressFinalize(game);
        Set(game, "_session", session); Set(game, "_shopCatalog", new ShopCatalog([Hull], [Part(10, kind), Part(20, 6)]));
        var stateField = typeof(Game1).GetField("_state", Private)!;
        stateField.SetValue(game, Enum.Parse(stateField.FieldType, "Flying")); Set(game, "_selectedCargoMount", -1);
        Invoke(game, "SwapFlightEquipment", action);
        Wait(() => { lock (session.Gate) return session.World.Ships[720].ReloadCount == 1; });
        Assert.True(site.Packets.TryDequeue(out var packet));
        Assert.Equal(FedPacket.Pack([new byte[] { 10, 0, 34, 0, 0, 0, (byte)kind, 0, 255, 0 }]), packet);
        Assert.True(site.Packets.TryDequeue(out packet));
        // Native ReloadData = short amount + signed mount + pad; ship ID ignored on request.
        byte amount = (byte)(kind == 7 ? 50 : 8);
        Assert.Equal(FedPacket.Pack([new byte[] { 14, 0, 129, 0, 10, 0, 4, 0, 0, 0, amount, 0, 251, 0 }]), packet);
        lock (session.Gate)
        {
            var ship = session.World.Ships[720];
            if (kind == 7) Assert.Equal(10, Assert.Single(ship.Cargo!).Amount);
            else { Assert.Empty(ship.Cargo!); Assert.Equal(10, Assert.Single(ship.Loadout!).Amount); }
        }
        Assert.Empty(site.Packets);
    }

    [MemberData(nameof(Commands))]
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    public void Production_swap_reaches_real_server_and_returns_authoritative_inventory(string action, int kind, int mount)
    {
        Allegiance.TestInfra.Require.Artwork();
        using var server = new Allegiance.Server.Server { SpawnInitialMinersAtBoot = false };
        DataHullTypeIGC hull = default;
        Set(server, "ShipSpawner", (Func<Allegiance.Core.Model.StaticCore, Mission, short, (Ship, Sector)>)((core, mission, id) =>
        {
            hull = core.HullTypes.Values.First(h => h.MaxFixedWeapons > 0 && !h.HasCapability(DataHullTypeIGC.HabmLifepod)) with
            { HullId = 720, MaxWeapons = 8, MaxFixedWeapons = 4, Hardpoints = Hull.Hardpoints, PmEquipment = Hull.PmEquipment };
            ((Dictionary<short, DataHullTypeIGC>)core.HullTypes)[720] = hull;
            var ship = new Ship(id, hull, new Vector3(15000, 12000, 16000), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            mission.PlaceShip(ship, mission.Sectors[0]); return (ship, mission.Sectors[0]);
        }));
        server.Boot(new Allegiance.Server.ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork),
            StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
            Games = [new() { Name = "P720", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] }]
        });
        server.Missions[0].Stage = MissionStage.Started;
        using var session = new ConnectionSession(); session.Connect(server.BoundPort, "p720-human");
        session.World.PartTypeResolver = id => Part(id, kind);
        session.SendLogonRequest(new() { CharacterName = "p720-human", FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw", Time = 1, DwCookie = 720,
            CrcFileList = -1, SteamAuthTicket = new byte[16], SteamID = 72001 });
        void Pump(Func<bool> done)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < 5)
            { server.TickSim(); lock (session.Gate) if (done()) return; Thread.Sleep(1); }
            Assert.Fail("P720 real server transition timed out");
        }
        Pump(() => session.World.MyShipId is { } id && session.World.Ships.TryGetValue(id, out var e) && e.Latest is not null);
        var own = server.Missions.SelectMany(m => m.Sectors).SelectMany(s => s.Models.OfType<Ship>()).Single(s => s.Id == session.World.MyShipId);
        own.StationId = null; own.Position = new Vector3(15000, 12000, 16000);
        own.MountPart(Part(10, kind), 1, (byte)mount, 240);
        Assert.True(own.TryMountCargoPart(Part(11, kind), 1, -2, 240));
        Pump(() => session.World.Ships[own.Id].Cargo?.Any(p => p.PartId == 11) == true);
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1)); GC.SuppressFinalize(game);
        Set(game, "_session", session); Set(game, "_shopCatalog", new ShopCatalog([hull], [Part(10, kind), Part(11, kind)]));
        var stateField = typeof(Game1).GetField("_state", Private)!;
        stateField.SetValue(game, Enum.Parse(stateField.FieldType, "Flying")); Set(game, "_selectedCargoMount", -2);
        Invoke(game, "SwapFlightEquipment", action);
        Pump(() => session.World.Ships[own.Id].Loadout?.Any(p => p.PartId == 11 && p.MountId == mount) == true);
        Assert.Equal(11, own.Parts.Single(p => p.EquipmentType == (EquipmentType)kind).PartId);
        Assert.Equal(10, own.CargoHold[Ship.CargoSlotForMount(-2)]!.PartId);
        lock (session.Gate) Assert.Equal(10, Assert.Single(session.World.Ships[own.Id].Cargo!).PartId);
        Assert.Equal(0, server.SimPassStageFailures);
    }

}
