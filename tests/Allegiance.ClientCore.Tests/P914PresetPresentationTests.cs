using System.Reflection;
using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P914PresetPresentationTests
{
    [SkippableTheory]
    [InlineData(HudStyle.Classic, "unlocked", false)]
    [InlineData(HudStyle.Modern, "unlocked", true)]
    [InlineData(HudStyle.Classic, "research", true)]
    [InlineData(HudStyle.Modern, "research", false)]
    public void Skirmish_config_and_wire_result_reach_both_styles_independent_of_deadline_flag(
        HudStyle style, string techMode, bool enabled)
    {
        Require.Artwork();
        var options = new SkirmishOptions { TechMode = techMode, StationKillsDecideDeadline = enabled };
        var config = options.CreateConfig(ExternalPaths.Resolve(ExternalResourceKind.Artwork));
        Assert.True(config.Games[0].SkirmishPreset);
        Assert.Equal(enabled, config.Showcase.StationKillsDecideDeadline);
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(config);
        var mission = server.Missions[0];
        Assert.True(mission.IsShowcasePreset);
        var sector = mission.Sectors[0];
        var ship = new Ship(29000, mission.Core.HullTypes.Values.First(), Vector3.Zero,
            Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, sector);
        var type = mission.Core.StationTypes.Values.First(t => t.MaxShieldHitPoints > 0 && t.MaxArmorHitPoints > 0
            && !t.HasCapability(DataStationTypeIGC.SabmPedestal));
        var station = new Station(new DataStationIGC([0, 0, 0], [0, 1, 0], [1, 0, 0],
            [0, 0, 0, 1], sector.Id, 1, 30000, type.StationTypeId, 255, 255, "Target"), type);
        mission.PlaceStation(station, sector);
        byte damage = mission.Core.ProjectileTypes.Values.First(p => p.Power > 0
            && mission.Core.Constants.DamageConstants[p.DamageType & 0x1f][type.DefenseTypeShield] > 0
            && mission.Core.Constants.DamageConstants[p.DamageType & 0x1f][type.DefenseTypeArmor] > 0).DamageType;
        Assert.Equal(DamageResult.Killed, station.ReceiveDamage(damage, 1e9f, mission.LastUpdate,
            station.Center, station.Center, ship));
        // Production server result encoder, then the same wire decoder and shared
        // models that Game1 sends to its renderer for either skin.
        var packet = (byte[])typeof(Srv).GetMethod("EncodeGameOverResult", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [mission, (short)1, "The clock has stopped. The winner: Enemy by kills"])!;
        var result = GameOver.Decode(FedPacket.Unpack(packet).Single(m => FedMessageHeader.Read(m).Fmid == GameOver.Fmid));
        var settings = new HudSettings { Style = style };
        Assert.Equal(style, settings.Style);
        var panel = GameOverPanelInfo.From(result, 0);
        Assert.Equal(GameOverOutcome.Defeat, panel.Outcome); // D never changes the result
        Assert.Equal("The clock has stopped. The winner: Enemy by kills", panel.ReasonLines[0]);
        Assert.Equal(mission.StationKillSummary(), panel.ReasonLines[1..]);
        var rows = GameOverTablesLayout.BuildSides(result.Sides, result.NumSides,
            new(GameOverTablesLayout.SideColumn.Position, false));
        Assert.Equal(1, rows.Single(r => r.SideId == 0).BaseKills);
        Assert.Equal(0, rows.Single(r => r.SideId == 1).BaseKills);
    }

    [Fact]
    public void Skirmish_default_is_off_and_LAN_does_not_claim_the_offline_preset()
    {
        Assert.False(new SkirmishOptions().StationKillsDecideDeadline);
        var lan = new SkirmishOptions { HostLan = true, StationKillsDecideDeadline = true }.CreateConfig();
        Assert.True(lan.Showcase.StationKillsDecideDeadline);
        Assert.False(lan.Games[0].SkirmishPreset);
    }
}
