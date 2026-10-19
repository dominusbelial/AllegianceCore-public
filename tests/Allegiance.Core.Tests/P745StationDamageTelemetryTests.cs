using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P745StationDamageTelemetryTests
{
    private sealed class SiegeSite : IIgcSite
    {
        internal readonly List<string> Lines = [];
        public void ReportNotable(string text) => Lines.Add(text);
    }

    [SkippableFact]
    public void Resolved_siege_reports_weapon_and_pool_changes_throttles_hits_and_always_reports_death()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var site = new SiegeSite();
        var target = core.StationTypes[core.Civilizations[25].InitialStationTypeId];
        var home = new DataStationIGC([0,0,0], [0,1,0], [0,0,-1], [0,0,0,1], 1,0,101,target.StationTypeId,255,255,"siege");
        var map = MissionMap.FromLists([new DataClusterIGC(0,[0,1,0],0,0,0,1,0,0,"test","","",0,0,0,true,false)], [], [], []);
        var mission = Mission.Assemble(core, map, [], site);
        var station = new Station(home, target) { Mission = mission };
        var shooter = new Ship(1, core.HullTypes[124], new(0,0,1000), Vector3.Zero, Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(shooter, mission.Sectors[0]);
        var part = core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Weapon w
            && core.ProjectileTypes[w.ProjectileTypeId] is var pt && pt.Power > 0
            && core.Constants.DamageConstants[pt.DamageType & 0x1f][target.DefenseTypeShield] > 0);
        var gun = (DataPartTypeIGC.Weapon)part.Type;
        var round = core.ProjectileTypes[gun.ProjectileTypeId];
        var projectile = new Projectile(2, mission.Sectors[0], round, shooter.Position, Vector3.Zero, new(0,0,-1), 0, shooter);
        void Hit(double now, float power)
        {
            float shield = station.ShieldFraction, hull = station.ArmorFraction;
            station.ReceiveDamage(round.DamageType, power, now, station.Center, station.Center, shooter);
            station.TraceDamage(now, shooter, round.DamageType, shield, hull, projectile);
        }
        Hit(0, 0); Assert.Empty(site.Lines);
        float hit = target.MaxShieldHitPoints / core.Constants.DamageConstants[round.DamageType & 0x1f][target.DefenseTypeShield] * .1f;
        Hit(0, hit);
        var line = Assert.Single(site.Lines);
        Assert.Contains("[station-damage]", line); Assert.Contains("side=0 station=101:siege", line);
        Assert.Contains("ship=1 hull=124:Bomber", line); Assert.Contains($"projectile={round.ProjectileTypeId}", line);
        Assert.Contains("shield=1.0000->0.9000 hull=1.0000->1.0000", line);
        Hit(1, hit); Assert.Single(site.Lines);
        Hit(5, hit); Assert.Equal(2, site.Lines.Count);
        // A lethal hit must still be visible inside the rate-limit window.
        byte lethal = core.ProjectileTypes.Values.First(pt => pt.Power > 0
            && core.Constants.DamageConstants[pt.DamageType & 0x1f][target.DefenseTypeShield] > 0
            && core.Constants.DamageConstants[pt.DamageType & 0x1f][target.DefenseTypeArmor] > 0).DamageType;
        float sb = station.ShieldFraction, hb = station.ArmorFraction;
        Assert.Equal(DamageResult.Killed, station.ReceiveDamage(lethal, 1e8f, 6, station.Center, station.Center, shooter));
        station.TraceDamage(6, shooter, lethal, sb, hb);
        Assert.Equal(3, site.Lines.Count); Assert.Contains("killed=True", site.Lines[^1]);
    }
}
