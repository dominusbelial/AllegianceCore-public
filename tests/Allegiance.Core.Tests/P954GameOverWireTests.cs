using System.Reflection;
using System.Text.Json;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P954 — wire-level pin of the GAME_OVER(62) message text against native's
/// <c>CFSMission::QueueGameoverMessage</c> (FedSrv/fsmission.cpp:3059). Native
/// puts exactly one string on the wire: <c>m_pszReason</c> verbatim, or
/// "Your side has been eliminated." when null (fsmission.cpp:3063-3065,
/// <c>FM_VAR_PARM</c>); the same builder serves the full-mission queue
/// (fsmission.cpp:3437) and the side-only queue (fsmission.cpp:5425). A timed
/// match's reason is ranked from kills/deaths at fsmission.cpp:2468-2498 and
/// is either "The clock has stopped. The winner: &lt;side&gt; by &lt;clue&gt;"
/// (:2497) or "The clock has stopped. The game is a draw." (:2502). The port
/// adds nothing for ordinary missions; showcase presets append exactly the
/// approved P914 station-kill tally (docs/reports/P914_REPORT.md, "Feed and
/// score overlay"), with the native reason still leading the message.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P954GameOverWireTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private static (Srv Server, Mission Mission) Boot(bool preset)
    {
        Require.Artwork();
        var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = new ServerConfig
        {
            Port = 0, ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc",
            Games = [new() { Name = "P954", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
                SideNames = ["Alpha", "Bravo"], AutoStart = false, SkirmishPreset = preset }]
        };
        server.Boot(JsonSerializer.Deserialize(JsonSerializer.Serialize(config, ServerJsonContext.Default.ServerConfig),
            ServerJsonContext.Default.ServerConfig)!);
        return (server, server.Missions[0]);
    }

    private static Ship Launcher(Mission mission, short side)
    {
        var ship = new Ship((short)(29000 + side), mission.Core.HullTypes.Values.First(),
            new Vector3(20000, 20000, 20000), Vector3.Zero, Orientation.Identity) { SideId = side };
        mission.PlaceShip(ship, mission.Sectors[0]);
        return ship;
    }

    private static void Destroy(Mission mission, Ship launcher)
    {
        var type = mission.Core.StationTypes.Values.First(t => t.MaxArmorHitPoints > 0
            && t.MaxShieldHitPoints > 0 && !t.HasCapability(DataStationTypeIGC.SabmPedestal))
            with { SabmCapabilities = 0 };
        var station = new Station(new DataStationIGC([100000, 100000, 0], [0, 1, 0], [1, 0, 0],
            [0, 0, 0, 1], mission.Sectors[0].Id, 1, 30000, type.StationTypeId, 255, 255, "P954 base"), type);
        mission.PlaceStation(station, mission.Sectors[0]);
        byte damage = mission.Core.ProjectileTypes.Values.First(p => p.Power > 0
            && mission.Core.Constants.DamageConstants[p.DamageType & 0x1f][type.DefenseTypeShield] > 0
            && mission.Core.Constants.DamageConstants[p.DamageType & 0x1f][type.DefenseTypeArmor] > 0).DamageType;
        Assert.Equal(DamageResult.Killed, station.ReceiveDamage(damage, 1e9f, mission.LastUpdate,
            station.Center, station.Center, launcher));
        Assert.Equal(1, mission.StationKills(0));
    }

    private static GameOver Encode(Srv server, Mission mission, short winner, string reason) =>
        GameOver.Decode(FedPacket.Unpack((byte[])typeof(Srv).GetMethod("EncodeGameOverResult", Hidden)!
            .Invoke(server, [mission, winner, reason])!).Single(m => FedMessageHeader.Read(m).Fmid == GameOver.Fmid));

    [SkippableFact]
    public void Ordinary_mission_sends_the_native_reason_verbatim_with_no_tally()
    {
        var (server, mission) = Boot(preset: false);
        using (server)
        {
            Destroy(mission, Launcher(mission, 0));
            string reason = "The clock has stopped. The winner: Alpha by kills"; // fsmission.cpp:2497
            var result = Encode(server, mission, 0, reason);
            // Native sends m_pszReason alone (fsmission.cpp:3063-3065): no appended lines.
            Assert.Equal(reason, result.GameoverMessage);
            Assert.DoesNotContain("Station kills:", result.GameoverMessage);
            Assert.DoesNotContain("Enemy base shattered:", result.GameoverMessage);
        }
    }

    [SkippableFact]
    public void Showcase_preset_leads_with_the_native_reason_then_the_approved_p914_tally()
    {
        var (server, mission) = Boot(preset: true);
        using (server)
        {
            Destroy(mission, Launcher(mission, 0));
            string reason = "The clock has stopped. The game is a draw."; // fsmission.cpp:2502
            var result = Encode(server, mission, -1, reason);
            string[] lines = result.GameoverMessage.Split('\n');
            Assert.Equal(reason, lines[0]);
            Assert.Equal(mission.StationKillSummary(), lines[1..]);
            Assert.Equal(mission.StationKills(0), result.Sides[0].CBaseKills);
        }
    }
}
