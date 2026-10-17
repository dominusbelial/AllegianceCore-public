using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P722InterfaceModelTests
{
    private static ClientWorld Roster()
    {
        var world = new ClientWorld();
        world.Apply(new JoinedMission { ShipId = 10 });
        // SET_CLUSTER assigns MyShipId even without a static core.
        var payload = new byte[ServerShipUpdate.Width]; payload[0] = 10;
        var reader = new WireReader(payload);
        world.Apply(new SetCluster { SectorId = 3, Ship = ServerShipUpdate.DecodeBody(ref reader) });
        foreach (short id in new short[] { 10, 11, 12 })
            world.Apply(new PlayerInfo { ShipId = id, ISide = id == 12 ? (short)1 : (short)0, CharacterName = "Pilot " + id });
        world.Apply(new SetTeamLeader { SideId = 0, ShipId = 10 });
        return world;
    }

    [Fact]
    public void Boot_requires_confirmation_and_rechecks_live_leadership_and_side()
    {
        var world = Roster(); var ui = new ModerationModel();
        Assert.True(ui.Handle("/boot 11", world, out var boot)); Assert.Null(boot);
        Assert.Contains("Boot Pilot 11?", ui.Notice);
        Assert.True(ui.Handle("/confirmboot", world, out boot)); Assert.Equal((short)11, boot);
        ui.Handle("/confirmboot", world, out boot); Assert.Null(boot);
        ui.Handle("/boot 11", world, out _); ui.Handle("/cancelboot", world, out _);
        ui.Handle("/confirmboot", world, out boot); Assert.Null(boot);
        ui.Handle("/boot 11", world, out _); ui.Reset();
        ui.Handle("/confirmboot", world, out boot); Assert.Null(boot);
        ui.Handle("/boot 11", world, out _);
        world.Apply(new SetTeamLeader { SideId = 0, ShipId = 11 });
        ui.Handle("/confirmboot", world, out boot); Assert.Null(boot);
        world.Apply(new SetTeamLeader { SideId = 0, ShipId = 10 });
        ui.Handle("/boot 12", world, out _); ui.Handle("/confirmboot", world, out boot); Assert.Null(boot);
        ui.Handle("/boot 10", world, out _); ui.Handle("/confirmboot", world, out boot); Assert.Null(boot);
        Assert.False(ui.Handle("normal team chat", world, out _));
    }

    [Fact]
    public void Local_mute_filters_incoming_chat_and_unmute_restores_it_without_a_wire_MUTE()
    {
        var world = Roster(); var ui = new ModerationModel();
        ui.Handle("/mute 11", world, out var boot); Assert.Null(boot);
        world.Apply(new ChatMessage { SidSender = 11, Message = "muted" });
        Assert.DoesNotContain(world.Events.Chats, m => m.Message == "muted");
        world.Apply(new ChatMessage { SidSender = -1, Message = "server notice" });
        Assert.Contains(world.Events.Chats, m => m.Message == "server notice");
        ui.Handle("/unmute 11", world, out _);
        world.Apply(new ChatMessage { SidSender = 11, Message = "audible" });
        Assert.Contains(world.Events.Chats, m => m.Message == "audible");
        ui.Handle("/mute 10", world, out _); Assert.False(world.IsPilotMuted(10));
        world.SetPilotMuted(11, true);
        world.Reset(); Assert.False(world.IsPilotMuted(11));
    }

    [Fact]
    public void Selection_uses_live_station_health_and_asteroid_ore_without_inventing_ship_fields()
    {
        var world = Roster();
        var station = DataStationIGC.Decode(new byte[DataStationIGC.Size]) with { StationId = 20, Name = "Base" };
        world.Apply(ExportStation.FromStation(station));
        world.Apply(new StationsUpdate { Entries = [new(20, 120, 60)] });
        var info = SelectionPaneModel.Build(world, IgcObjectType.Station, 20)!.Value;
        Assert.Equal("Base", info.Name); Assert.Equal(0.5f, info.Hull); Assert.Equal(0.25f, info.Shield);
        Assert.Null(info.Enemy); Assert.Null(info.Ore);
        var asteroid = DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with { AsteroidId = 21, OreMax = 240 };
        world.Apply(ExportAsteroid.FromAsteroid(asteroid));
        world.Apply(new AsteroidsUpdate { Entries = [new(21, CompactOrientation.Identity, 60, 120)] });
        info = SelectionPaneModel.Build(world, IgcObjectType.Asteroid, 21)!.Value;
        Assert.Equal(0.5f, info.Hull); Assert.Equal(0.25f, info.Ore); Assert.Null(info.Shield);
        Assert.Null(SelectionPaneModel.Build(world, IgcObjectType.Ship, 30000));
        world.Apply(new StationDestroyed { StationId = 20 });
        Assert.Null(SelectionPaneModel.Build(world, IgcObjectType.Station, 20)!.Value.Hull);
    }

    [Fact]
    public void Selection_treats_allies_as_friendly_and_drops_dead_or_departed_targets()
    {
        var world = Roster();
        Assert.True(SelectionPaneModel.Build(world, IgcObjectType.Ship, 12)!.Value.Enemy);
        world.Apply(new ChangeAlliances { Allies = [0, 0, -1, -1, -1, -1] });
        Assert.False(SelectionPaneModel.Build(world, IgcObjectType.Ship, 12)!.Value.Enemy);
        world.Apply(new ShipDelete { ShipId = 12, Sdr = ShipDelete.SdrLeftSector });
        Assert.Null(SelectionPaneModel.Build(world, IgcObjectType.Ship, 12));
        world.Apply(new KillShip { ShipId = 11, IdCredit = -1, SideIdKiller = -1, BDeathCredit = true });
        Assert.Null(SelectionPaneModel.Build(world, IgcObjectType.Ship, 11));
    }

    [Fact]
    public void Quit_side_clears_own_relation_until_authoritative_join_and_retains_other_pilot()
    {
        var prelaunch = new ClientWorld();
        prelaunch.Apply(new JoinedMission { ShipId = 10 });
        prelaunch.Apply(new PlayerInfo { ShipId = 10, ISide = 0 });
        prelaunch.Apply(new QuitSide { ShipId = 10, Reason = QuitSideReason.SwitchingSides });
        Assert.Null(prelaunch.MySideId);
        var world = Roster();
        world.Apply(new QuitSide { ShipId = 10, Reason = QuitSideReason.SwitchingSides });
        Assert.Null(world.MySideId); Assert.Equal((short)-2, world.Ships[10].SideId);
        Assert.True(world.Ships.ContainsKey(11));
        world.Apply(new JoinSide { ShipId = 10, SideId = 1 });
        Assert.Equal((short)1, world.MySideId);
        Assert.False(SelectionPaneModel.Build(world, IgcObjectType.Ship, 12)!.Value.Enemy);
    }

    [Theory]
    [InlineData(8, 15)] // c_habmCaptureThreat -> station capture warning
    [InlineData(512, 13)] // c_habmThreatToStation -> station threatened
    public void Threat_abilities_produce_the_native_client_warning_text(short ability, short warning)
    {
        // igc.h:5384-5394 / common.cpp:2490-2500; the UI consumes the
        // classified warning, it must never treat CaptureThreat as boarding.
        var hull = DataHullTypeIGC.Decode(new byte[DataHullTypeIGC.Size]) with { HabmCapabilities = ability };
        var ship = new Allegiance.Core.Sim.Ship(30, hull, default, default, Allegiance.Core.Sim.Orientation.Identity);
        short assets = (short)(ClusterWarnings.AmStation | ClusterWarnings.GetAssetMask(ship, false));
        Assert.Equal(warning, ClusterWarnings.GetWarning(assets, false));
        var world = new ClientWorld(); world.Apply(ClusterWarningUpdate.From(3, warning));
        Assert.Equal(warning, world.ClusterWarningFor(3));
        Assert.Contains(world.Events.Chats, c => c.Message.Contains(warning == 15 ? "capture" : "risk"));
    }
}
