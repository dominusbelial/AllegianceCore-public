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

/// <summary>P641 production send → live loopback server → real receive.</summary>
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
                var ship = new Ship(id, hull, new Vector3(station.Position[0] + 25, station.Position[1], station.Position[2]), default, Orientation.Identity) { SideId = station.SideId };
                own ??= ship;
                mission.PlaceShip(ship, sector);
                return (ship, sector);
            });
        server.Boot(new Allegiance.Server.ServerConfig { ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork),
            StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
            SideSelectAutoSeatSeconds = 30,   // P763: the observer tail never sends POSITIONREQ
            LiveSettings = new Allegiance.Server.LiveSettingsConfig { TestHarness = new Allegiance.Server.TestHarnessConfig { Enabled = true, Money = 1_000_000, IgnoreTechGate = true } },
            Games = [new Allegiance.Server.GameConfig { Name = "P641", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] }] });
        foreach (var mission in server.Missions) mission.Stage = MissionStage.Started;
        using var session = new ConnectionSession();
        session.World.PartTypeResolver = id => coreTypes.TryGetValue(id, out var p) ? p : null;
        session.Connect(server.BoundPort, "mcp-p641");
        session.SendLogonRequest(new LogonRequest { CharacterName = "mcp-p641", ZoneTicket = [1, 2, 3, 4],
            CDKey = "ABCDEFGHIJKL", MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer,
            Time = 641, DwCookie = 641, CrcFileList = -1, SteamAuthTicket = new byte[16],
            SteamAuthTicketLength = 0, SteamID = 97700000000000641ul, DrmHash = "p641" });
        void Wait(string phase, Func<bool> condition)
        {
            for (int i = 0; i < 6000; i++) { server.TickSim(); if (condition()) return; Thread.Sleep(1); }
            Assert.True(condition(), $"P641 live condition timed out: {phase}");
        }
        bool Has(ConnectionSession client, short part, bool cargo) { lock (client.Gate) return client.World.Ships.TryGetValue(own.Id, out var ship)
            && (cargo ? ship.Cargo : ship.Loadout)?.Any(p => p.PartId == part) == true; }
        Wait("seat and dock", () => session.World.MyShipId is not null && own.StationId is not null);
        using var observer = new ConnectionSession();
        observer.World.PartTypeResolver = session.World.PartTypeResolver;
        observer.Connect(server.BoundPort, "p660b-observer");
        observer.SendLogonRequest(new LogonRequest { CharacterName = "p660b-observer", ZoneTicket = [1, 2, 3, 4],
            CDKey = "ABCDEFGHIJKL", MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer,
            Time = 641, DwCookie = 641, CrcFileList = -1, SteamAuthTicket = new byte[16],
            SteamAuthTicketLength = 0, SteamID = 97700000000000642ul, DrmHash = "p660b" });
        Wait("observer side selection", () => { lock (observer.Gate) return observer.World.SideRoster is not null; });
        observer.SendPositionReq(own.SideId!.Value);
        Wait("Started mission and observer export", () =>
        {
            lock (session.Gate) lock (observer.Gate)
                return session.World.Mission?.Stage == MissionStageMessage.StageStarted
                    && observer.World.Mission?.Stage == MissionStageMessage.StageStarted
                    && observer.World.Ships.ContainsKey(own.Id);
        });
        bool ClientHas(short part, bool cargo) => Has(session, part, cargo) && Has(observer, part, cargo);
        ExpandedPartData[] loadout = [new(weapon.PartId, 1, 0, 240), new(spare.PartId, 1, 255, 240), new(booster.PartId, 1, 0, 240)];
        session.SendBuyLoadout(own.HullType.HullId, true, loadout);
        Wait("buy and launch", () => own.StationId is null && ClientHas(weapon.PartId, false) && ClientHas(spare.PartId, true));
        Assert.Equal(spare.PartId, own.CargoHold[Ship.CargoSlotForMount(-1)]!.PartId);
        Assert.DoesNotContain(own.Parts, p => p.PartId == spare.PartId);
        session.SendSwapPart(EquipmentType.Weapon, 0, -2); // UnmountPart
        Wait("unmount", () => ClientHas(weapon.PartId, true) && !ClientHas(weapon.PartId, false));
        Assert.DoesNotContain(own.Parts, p => p.EquipmentType == EquipmentType.Weapon);
        session.SendSwapPart(EquipmentType.Weapon, -2, 0); // MountPart
        Wait("mount", () => ClientHas(weapon.PartId, false));
        session.SendSwapPart(EquipmentType.Weapon, -1, 0); // ReplacePart
        Wait("replace", () => ClientHas(spare.PartId, false) && ClientHas(weapon.PartId, true));
        Assert.Equal(spare.PartId, own.Parts.Single(p => p.EquipmentType == EquipmentType.Weapon).PartId);
        for (int mount = 0; mount < 4; mount++)
        {
            var selected = FlightInput.ToggleWeapon(true, 0, mount, 4, true);
            int bits = FlightInput.FireContribution(true, selected.GroupFire, selected.SelectedMount);
            session.CommandControls(ControlData.Neutral, bits);
            Wait($"select weapon {mount}", () => (own.StateM & (ShipControlState.OneWeapon | ShipControlState.SelectedWeaponMask)) == bits);
        }
        float fuel = own.Fuel;
        session.CommandControls(ControlData.Neutral, ShipControlState.AfterburnerButton);
        Wait("booster fuel consumption", () => own.Fuel < fuel);
        session.CommandControls(ControlData.Neutral);
        Wait("booster release", () => (own.StateM & ShipControlState.AfterburnerButton) == 0);
        session.SendDropPart(EquipmentType.Weapon, 0); // DropPart
        Wait("drop", () => !Has(session, spare.PartId, false) && !Has(observer, spare.PartId, false)
            && !own.Parts.Any(p => p.EquipmentType == EquipmentType.Weapon));
        // The command scope must leave ordinary site-event broadcasts enabled.
        own.MountPart(spare, 1, 1, 240);
        Wait("ordinary loadout change after commands", () => ClientHas(spare.PartId, false));
    }
}
