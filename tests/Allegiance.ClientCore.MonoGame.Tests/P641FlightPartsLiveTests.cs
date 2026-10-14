using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Xunit;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>P641 production send → live server → real receive; run on the unrestricted runner.</summary>
[Collection("LiveNetwork")]
public class P641FlightPartsLiveTests
{
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Production_inventory_commands_weapon_selection_and_booster_roundtrip()
    {
        Require.Artwork();
        using var server = new Srv();
        Ship own = null!;
        DataPartTypeIGC weapon = default, spare = default, booster = default;
        var coreTypes = new Dictionary<short, DataPartTypeIGC>();
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server,
            (StaticCore core, Mission mission, short id) =>
            {
                var hull = core.HullTypes.Values.First(h => h.MaxFixedWeapons >= 2 && !h.HasCapability(DataHullTypeIGC.HabmLifepod)
                    && core.PartTypes.Values.Count(p => p.EquipmentType == EquipmentType.Weapon && InFlightPartRules.CanMount(h, p, 0)) >= 2
                    && core.PartTypes.Values.Any(p => p.EquipmentType == EquipmentType.Afterburner && h.CanMountPart(p)));
                var guns = core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon && InFlightPartRules.CanMount(hull, p, 0)).ToArray();
                weapon = guns[0]; spare = guns[1];
                booster = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Afterburner && hull.CanMountPart(p));
                foreach (var p in core.PartTypes) coreTypes[p.Key] = p.Value;
                var station = P550MonoTestDockHome.CapLandHome(mission, mission.Sides[0].Id);
                var sector = mission.Sectors.First(s => s.Stations.Contains(station));
                own = new Ship(id, hull, new Vector3(station.Position[0] + 25, station.Position[1], station.Position[2]), default, Orientation.Identity) { SideId = station.SideId };
                mission.PlaceShip(own, sector);
                return (own, sector);
            });
        server.Boot(new Allegiance.Server.ServerConfig { ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork),
            StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
            LiveSettings = new Allegiance.Server.LiveSettingsConfig { TestHarness = new Allegiance.Server.TestHarnessConfig { Enabled = true, Money = 1_000_000, IgnoreTechGate = true } },
            Games = [new Allegiance.Server.GameConfig { Name = "P641", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] }] });
        foreach (var mission in server.Missions) mission.Stage = MissionStage.Started;
        using var session = new ConnectionSession();
        session.World.PartTypeResolver = id => coreTypes.TryGetValue(id, out var p) ? p : null;
        session.Connect(server.BoundPort, "p641");
        session.SendLogonRequest(new LogonRequest { CharacterName = "p641", ZoneTicket = [1, 2, 3, 4],
            CDKey = "ABCDEFGHIJKL", MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer,
            Time = 641, DwCookie = 641, CrcFileList = -1, SteamAuthTicket = new byte[16],
            SteamAuthTicketLength = 0, SteamID = 97700000000000641ul, DrmHash = "p641" });
        void Wait(Func<bool> condition)
        {
            for (int i = 0; i < 6000; i++) { server.TickSim(); if (condition()) return; Thread.Sleep(1); }
            Assert.True(condition(), "P641 live condition timed out");
        }
        bool ClientHas(short part, bool cargo) { lock (session.Gate) return session.World.MyShipId is { } id
            && session.World.Ships.TryGetValue(id, out var ship) && (cargo ? ship.Cargo : ship.Loadout)?.Any(p => p.PartId == part) == true; }
        Wait(() => session.World.MyShipId is not null && own.StationId is not null);
        ExpandedPartData[] loadout = [new(weapon.PartId, 1, 0, 240), new(spare.PartId, 1, 255, 240), new(booster.PartId, 1, 0, 240)];
        session.SendBuyLoadout(own.HullType.HullId, true, loadout);
        Wait(() => own.StationId is null && ClientHas(weapon.PartId, false) && ClientHas(spare.PartId, true));
        session.SendSwapPart(EquipmentType.Weapon, 0, -2); // UnmountPart
        Wait(() => ClientHas(weapon.PartId, true) && !ClientHas(weapon.PartId, false));
        Assert.DoesNotContain(own.Parts, p => p.EquipmentType == EquipmentType.Weapon);
        session.SendSwapPart(EquipmentType.Weapon, -2, 0); // MountPart
        Wait(() => ClientHas(weapon.PartId, false));
        session.SendSwapPart(EquipmentType.Weapon, -1, 0); // ReplacePart
        Wait(() => ClientHas(spare.PartId, false) && ClientHas(weapon.PartId, true));
        Assert.Equal(spare.PartId, own.Parts.Single(p => p.EquipmentType == EquipmentType.Weapon).PartId);
        for (int mount = 0; mount < 4; mount++)
        {
            var selected = FlightInput.ToggleWeapon(true, 0, mount, 4, true);
            int bits = FlightInput.FireContribution(true, selected.GroupFire, selected.SelectedMount);
            session.CommandControls(ControlData.Neutral, bits);
            Wait(() => (own.StateM & (ShipControlState.OneWeapon | ShipControlState.SelectedWeaponMask)) == bits);
        }
        float fuel = own.Fuel;
        session.CommandControls(ControlData.Neutral, ShipControlState.AfterburnerButton);
        Wait(() => own.Fuel < fuel);
        session.CommandControls(ControlData.Neutral);
        Wait(() => (own.StateM & ShipControlState.AfterburnerButton) == 0);
        session.SendDropPart(EquipmentType.Weapon, 0); // DropPart
        Wait(() => !ClientHas(spare.PartId, false) && !own.Parts.Any(p => p.EquipmentType == EquipmentType.Weapon));
    }
}
