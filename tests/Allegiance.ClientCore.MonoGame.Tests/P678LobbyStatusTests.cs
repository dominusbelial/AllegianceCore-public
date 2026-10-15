using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P678LobbyStatusTests
{
    [Fact]
    public void Status_shows_authoritative_settings_and_requests_with_printable_names()
    {
        var world = new ClientWorld();
        world.Apply(new LockLobby { FLock = true });
        world.Apply(new LockSides { FLock = true });
        world.Apply(new AutoAccept { SideId = 1, FAutoAccept = false });
        world.Apply(new PlayerInfo { ShipId = 8, ISide = -2, CharacterName = "Pilot\u2603" });
        world.Apply(new PositionReqBroadcast { ShipId = 8, SideId = 1 });
        var lines = LobbyStatusLayout.Build(world);
        Assert.Contains("LOBBY: LOCKED  SIDE CHANGES: LOCKED", lines);
        Assert.Contains("SIDE 1 AUTO ACCEPT: OFF", lines);
        Assert.Contains(lines, l => l.Contains("awaiting approval for side 1"));
        Assert.All(lines.SelectMany(l => l), c => Assert.InRange(c, ' ', '~'));
        world.Apply(new DelPositionReq { ShipId = 8, SideId = 1 });
        Assert.DoesNotContain(LobbyStatusLayout.Build(world), l => l.Contains("awaiting approval"));
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Own_rejection_is_visible_before_launch_but_cancellation_and_other_players_are_quiet()
    {
        Require.Artwork();
        var world = new ClientWorld();
        world.Apply(new MissionDef { SzIGCStaticFile = "PCore001" });
        world.Apply(new JoinedMission { ShipId = 8 });
        world.Apply(new DelPositionReq { ShipId = 8, SideId = 1, Reason = DelPositionReqReason.TeamFull });
        Assert.Contains("You can't join this team - it's full.", LobbyStatusLayout.Build(world));
        world.Apply(new DelPositionReq { ShipId = 9, SideId = 1, Reason = DelPositionReqReason.Rejected });
        Assert.Contains("You can't join this team - it's full.", LobbyStatusLayout.Build(world));
        world.Apply(new PositionReqBroadcast { ShipId = 9, SideId = 1 });
        Assert.Contains("You can't join this team - it's full.", LobbyStatusLayout.Build(world));
        world.Apply(new DelPositionReq { ShipId = 9, SideId = 1 });
        world.Apply(new DelPositionReq { ShipId = 8, SideId = 1, Reason = DelPositionReqReason.Canceled });
        Assert.Single(LobbyStatusLayout.Build(world));
        world.Apply(new DelPositionReq { ShipId = 9, SideId = 1, Reason = DelPositionReqReason.TeamFull });
        Assert.Single(LobbyStatusLayout.Build(world));
        world.Apply(new DelPositionReq { ShipId = 8, SideId = 1, Reason = DelPositionReqReason.TeamFull });
        world.Apply(new PositionReqBroadcast { ShipId = 8, SideId = 1 });
        Assert.DoesNotContain("You can't join this team - it's full.", LobbyStatusLayout.Build(world));
    }

    [SkippableFact]
    public void Player_waiting_voice_uses_the_real_native_wave_and_content_pipeline()
    {
        Require.Artwork(); Require.NativeSource();
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        var content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
        string wave = LobbyStatusLayout.PlayerWaitingWave;
        Assert.Contains($"ImportWave(\"{wave}\")", File.ReadAllText(Path.Combine(content, "sounddef.mdl")));
        Assert.Contains($"/build:Sounds/{wave}.ogg", File.ReadAllText(Path.Combine(content, "Content.mgcb")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(ExternalPaths.Resolve(ExternalResourceKind.Artwork), wave + ".ogg")),
            File.ReadAllBytes(Path.Combine(content, "Sounds", wave + ".ogg")));
        Assert.Contains("PlaySoundEffect(salPlayerWaitingSound)", File.ReadAllText(Path.Combine(
            ExternalPaths.Resolve(ExternalResourceKind.NativeSource), "src", "clintlib", "appmsg.cpp")));
    }
}
