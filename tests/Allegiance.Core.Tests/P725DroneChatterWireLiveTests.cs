using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P725 — the drone/constructor in-flight chatter reaches a seated peer as a
/// TEAM chat carrying the corrected native SoundID as the wire
/// <c>VoiceOver</c>. This is the wire-level proof of the new
/// <see cref="IIgcSite.DroneChatEvent"/> seam: <c>Ship.SendDroneChat</c> →
/// <c>Mission.Site.DroneChatEvent</c> → <c>ServerHost.DroneChat</c> →
/// <c>Server.BroadcastDroneChat</c> → <c>BroadcastTeamChat</c>. The renderer
/// resolves the <c>VoiceOver</c> through ClientWorld and P732's native catalog,
/// so a correct id here means the chatter is audibly player-facing.
/// Failing-first: pre-P725 the chatter routed through the un-overridden
/// <c>IIgcSite.SendChat</c> no-op, so no chatter reached the receiver.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P725DroneChatterWireLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int ups = 20) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = ups,
        Games =
        [
            new Gm { Name = "P725 Drone Chatter", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 882u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 88700000000000002ul, DrmHash = "p725drone",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void SeatOne(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int n = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(n < 4000, $"{name} did not seat");
    }

    private static void InjectSingleMissionSpawn(Srv server)
    {
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(950f, 550f, 520f), Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            return (ship, mission.Sectors[0]);
        };
    }

    private static (Srv Server, FltBot Bot, Side Side, Mission Mission) BootOwnedSide()
    {
        var server = new Srv();
        InjectSingleMissionSpawn(server);
        server.SpawnInitialMinersAtBoot = false;   // P382: pristine arena
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        var bot = new FltBot();
        SeatOne(server, bot, "P725C");
        short sideId = SideOf(server, bot);
        var mission = server.Missions[0];
        var side = mission.Sides.Single(s => s.Id == sideId);
        return (server, bot, side, mission);
    }

    private static short SideOf(Srv server, FltBot bot)
    {
        var shipId = bot.ShipAssignment!.Ship.ShipId;
        return server.Ships.Single(s => s.Ship.Id == shipId).Ship.SideId!.Value;
    }

    /// <summary>
    /// A real miner's in-flight chatter edge (droneTooCrowdedSound, native
    /// shipIGC.cpp:2199) is heard by a seated teammate as a TEAM chat whose
    /// <c>VoiceOver</c> is the corrected SoundID 1342 — the exact id the
    /// renderer's <c>DroneChatterCue.Resolve</c> maps to a real clip
    /// (<c>vo_request_miner</c>).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Crowded_miner_chatter_reaches_seated_teammate_with_correct_voice_over()
    {
        Require.Artwork();
        var (server, bot, side, mission) = BootOwnedSide();
        using (server)
        using (bot)
        {
            var sector = mission.Sectors[0];
            var miner = server.SpawnNpcShip(mission, sector, side.Id, new Vector3(100f, 100f, 100f));
            Assert.Equal(Ship.PilotTypeMiner, miner.PilotType);

            // Invoke the real chatter edge once (past the 30 s cooldown), then
            // pump for the wire broadcast to reach the seated peer.
            miner.ComplainMiningCrowded(30.0);
            int rx = PumpUntil(server, () => bot.Chats.Any(c => c.VoiceOver == DroneChatterCue.DroneTooCrowdedSound), 800);
            Assert.True(rx < 800, "seated teammate never received the too-crowded drone chatter (VoiceOver==1342)");

            var chat = bot.Chats.Single(c => c.VoiceOver == DroneChatterCue.DroneTooCrowdedSound);
            Assert.Equal((int)ChatTarget.Team, chat.ChatTarget);
            Assert.Contains("Miner requesting unoccupied He3 asteriod.", chat.Message);
            var world = new Allegiance.ClientCore.State.ClientWorld();
            world.Apply(ChatMessage.Decode(chat.Encode()));
            var sound = Assert.Single(world.TakeSoundEvents());
            Assert.Equal(Allegiance.ClientCore.State.ClientSoundEventKind.ChatVoiceOver, sound.Kind);
            Assert.Equal("vo_request_miner", sound.Voice!.Stem);
            Assert.Empty(world.TakeSoundEvents());
            Assert.NotNull(DroneChatterCue.Resolve(chat.VoiceOver));   // the renderer can play it
            Console.WriteLine($"[P725] seated teammate heard drone chatter '{chat.Message}' (voice {chat.VoiceOver})");
        }
    }
}
