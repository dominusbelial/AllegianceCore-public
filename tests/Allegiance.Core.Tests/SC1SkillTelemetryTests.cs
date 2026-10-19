using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class SC1SkillTelemetryTests
{
    [Fact]
    public void Final_is_once_snapshots_do_not_reset_and_matches_sides_are_isolated()
    {
        var a = new SkillTelemetry(1, [0, 1]);
        var b = new SkillTelemetry(2, [0, 1]);
        a.Record(0, ShowcaseSkill.Missiles);
        a.Record(null, ShowcaseSkill.Missiles);
        a.Record(99, ShowcaseSkill.Missiles);
        using var log = new StringWriter();
        a.Write(log); a.Write(log, true); a.Write(log, true);
        a.Record(0, ShowcaseSkill.Missiles);
        Assert.Equal(1, a.Count(0, ShowcaseSkill.Missiles));
        Assert.Equal(0, a.Count(1, ShowcaseSkill.Missiles));
        Assert.Equal(0, b.Count(0, ShowcaseSkill.Missiles));
        Assert.Equal(1, log.ToString().Split('\n').Count(s => s == "[skills] match 1 side 0 final"));
        Assert.Equal((int)ShowcaseSkill.Count, SkillTelemetry.Catalogue.Length);
    }

    private static Allegiance.Server.Server Boot()
    {
        Require.Artwork();
        var server = new Allegiance.Server.Server { SpawnInitialMinersAtBoot = false };
        server.Boot(new ServerConfig { Port = 0, Games = [new()
        { Teams = 2, CivIds = [25, 3], SideNames = ["A", "B"], Map = "m2xspiderw.igc" }] });
        return server;
    }

    private static Ship Place(Mission m, short id, short side, byte pilot = Ship.PilotTypePlayer)
    {
        var hull = m.Core.HullTypes.Values.First(h => h.MaxEnergy > 100 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed);
        var ship = new Ship(id, hull, new(10000,10000,10000), Vector3.Zero, Orientation.Identity) { SideId = side, IsNpc = pilot != Ship.PilotTypePlayer, PilotType = pilot };
        m.PlaceShip(ship, m.Sectors[0]);
        return ship;
    }

    [SkippableFact]
    public void Successful_native_fire_sites_count_actual_rounds_and_expendables()
    {
        using var server = Boot(); var m = server.Missions[0]; var ship = Place(m, 29000, 0);
        ship.FireMissile(m.Core.MissileTypes.Values.First(), server.SimNow);
        ship.DeployMine(m.Core.MineTypes.Values.First(), server.SimNow);
        ship.DeployProbe(m.Core.ProbeTypes.Values.First(), server.SimNow);
        ship.DeployChaff(m.Core.ChaffTypes.Values.First(), server.SimNow);
        foreach (var skill in new[] { ShowcaseSkill.Missiles, ShowcaseSkill.Mines, ShowcaseSkill.Probes, ShowcaseSkill.Chaff })
        {
            Assert.Equal(1, m.Skills!.Count(0, skill));
            Assert.Equal(0, m.Skills.Count(1, skill));
        }
        var weaponType = m.Core.PartTypes.Values.Select(p => p.Type).OfType<DataPartTypeIGC.Weapon>()
            .First(w => w.EnergyPerShot < ship.HullType.MaxEnergy && w.CAmmoPerShot == 0);
        var weapon = ship.MountWeapon(weaponType);
        ship.StateM = ShipControlState.AllWeapons;
        weapon.Update(ship.LastUpdate + 2);
        Assert.Equal(ship.Sector!.Models.OfType<Projectile>().LongCount(), m.Skills!.Count(0, ShowcaseSkill.Guns));
        Assert.True(m.Skills.Count(0, ShowcaseSkill.Guns) > 0);
    }

    [SkippableFact]
    public void Damage_and_nanites_count_only_effective_hits_credit_the_source_and_keep_type()
    {
        using var server = Boot(); var m = server.Missions[0]; var source = Place(m, 29001, 0); var target = Place(m, 29002, 1);
        byte type = Enumerable.Range(0, DataConstantsIGC.DmgidMax).Select(i => (byte)i).First(i => m.Core.Constants.DamageConstants[i][target.HullType.DefenseType] > 0);
        target.ReceiveDamage(type, 10, 1, target.Position, Vector3.Zero, source);
        Assert.Equal(1, m.Skills!.Count(0, ShowcaseSkill.ShipDamage));
        target.ReceiveDamage(type, -100000, 2, target.Position, Vector3.Zero, source);
        target.ReceiveDamage(type, -100000, 3, target.Position, Vector3.Zero, source);
        Assert.Equal(1, m.Skills.Count(0, ShowcaseSkill.NaniteShip));
        source.ReceiveDamage(type, 10, 4, source.Position, Vector3.Zero, source); // friendly fire disabled
        Assert.Equal(1, m.Skills.Count(0, ShowcaseSkill.ShipDamage));
        var station = m.Sectors.SelectMany(s => s.StationRuntimes).First(s => s.SideId == 1 && s.HasShield);
        byte stationType = Enumerable.Range(0, DataConstantsIGC.DmgidMax).Select(i => (byte)i).First(i =>
            m.Core.Constants.DamageConstants[i][station.Type.DefenseTypeArmor] > 0 && m.Core.Constants.DamageConstants[i][station.Type.DefenseTypeShield] > 0);
        station.ReceiveDamage(stationType, 1000, 5, station.Center, Vector3.Zero, source);
        // A larger hit must penetrate shields before hull repair can have an effect.
        while (station.ArmorFraction == 1 && !station.Dead)
            station.ReceiveDamage(stationType, 1000, 5, station.Center, Vector3.Zero, source);
        Assert.True(m.Skills.Count(0, ShowcaseSkill.StationDamage) > 0);
        station.ReceiveDamage(stationType, -1000000, 6, station.Center, Vector3.Zero, source);
        station.ReceiveDamage(stationType, -1000000, 7, station.Center, Vector3.Zero, source);
        Assert.Equal(1, m.Skills.Count(0, ShowcaseSkill.NaniteStation));
        Assert.Equal(DamageResult.Killed, station.ReceiveDamage(stationType, 1000000000, 8, station.Center, Vector3.Zero, source));
        Assert.Equal(1, m.Skills.Count(0, ShowcaseSkill.StationKills));
        using var log = new StringWriter(); m.Skills.Write(log);
        Assert.Contains($"ship-{type} 2", log.ToString());
        Assert.Contains($"station-{stationType} ", log.ToString());
    }

    [SkippableFact]
    public void Cloak_counts_emitter_activation_once_and_unmounted_input_does_not_count()
    {
        using var server = Boot(); var m = server.Missions[0]; m.Stage = MissionStage.Started;
        var ship = Place(m, 29007, 0); ship.StateM = ShipControlState.CloakActive;
        ship.Update(ship.LastUpdate + .1);
        Assert.Equal(0, m.Skills!.Count(0, ShowcaseSkill.Cloak));
        var part = m.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Cloak { MaxCloaking: > 0 });
        ship.MountPart(part, 1, 0, 255);
        ship.Update(ship.LastUpdate + .1);
        Assert.True(ship.IsCloaked);
        ship.Update(ship.LastUpdate + .1);
        Assert.Equal(1, m.Skills.Count(0, ShowcaseSkill.Cloak));
        Assert.Equal(0, m.Skills.Count(1, ShowcaseSkill.Cloak));
    }

    [SkippableFact]
    public void Commander_order_counters_require_a_legal_executed_drone_command()
    {
        using var server = Boot(); var m = server.Missions[0];
        var sender = Place(m, 29003, 0); var drone = Place(m, 29004, 0, Ship.PilotTypeWingman); var enemy = Place(m, 29005, 1);
        var command = typeof(Allegiance.Server.Server).GetMethod("CommandToDrone", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Send(short cid, IgcObjectType type, short id) => command.Invoke(server,
            [new Allegiance.Core.Transport.ConnectionId(999), sender, drone, new Allegiance.Core.Wire.ChatMessage
            { CommandID = cid, OtTarget = (short)type, OidTarget = id }]);
        Send(Allegiance.Core.Wire.OrderChange.CidGoto, IgcObjectType.Ship, sender.Id);
        Send(Allegiance.Core.Wire.OrderChange.CidAttack, IgcObjectType.Ship, enemy.Id);
        Send(Allegiance.Core.Wire.OrderChange.CidRepair, IgcObjectType.Ship, sender.Id);
        Send(Allegiance.Core.Wire.OrderChange.CidRepair, IgcObjectType.Ship, enemy.Id); // illegal enemy repair order
        Send(Allegiance.Core.Wire.OrderChange.CidBuild, IgcObjectType.Ship, sender.Id); // illegal wingman build
        Assert.Equal(1, m.Skills!.Count(0, ShowcaseSkill.OrderGoto));
        Assert.Equal(1, m.Skills.Count(0, ShowcaseSkill.OrderAttack));
        Assert.Equal(1, m.Skills.Count(0, ShowcaseSkill.OrderRepair));
        Assert.Equal(0, m.Skills.Count(0, ShowcaseSkill.OrderBuild));
        drone = Place(m, 29006, 0, Ship.PilotTypeLayer);
        drone.LayerLaidExpendableTypeId = m.Core.MineTypes.Keys.First();
        Send(Allegiance.Core.Wire.OrderChange.CidBuild, IgcObjectType.Warp, m.Sectors[0].Warps[0].Warp.WarpId);
        Assert.Equal(1, m.Skills.Count(0, ShowcaseSkill.OrderBuild));
        Assert.Equal(4, m.Skills.Count(0, ShowcaseSkill.Voice));
        Assert.Equal(0, m.Skills.Count(1, ShowcaseSkill.OrderGoto));
    }

    [SkippableFact]
    public void Gameover_emits_once_rotation_resets_and_console_query_is_non_destructive()
    {
        using var server = Boot(); var m = server.Missions[0]; m.Stage = MissionStage.Started;
        m.Skills!.Record(0, ShowcaseSkill.Missiles);
        var saved = Console.Out; using var log = new StringWriter(); Console.SetOut(log);
        try
        {
            server.RequestSkills(); server.TickSim();
            var end = typeof(Allegiance.Server.Server).GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!;
            end.Invoke(server, [m, (short)0, "SC1 test"]);
            end.Invoke(server, [m, (short)0, "duplicate"]);
        }
        finally { Console.SetOut(saved); }
        Assert.Equal(1, log.ToString().Split('\n').Count(s => s == $"[skills] match {m.Skills.Match} side 0 final"));
        Assert.Contains("snapshot", log.ToString());
        Assert.NotSame(m, server.Missions[0]);
        Assert.Equal(0, server.Missions[0].Skills!.Count(0, ShowcaseSkill.Missiles));
        Assert.NotEqual(m.Skills.Match, server.Missions[0].Skills!.Match);
    }
}
