using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Identity;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P854 — private games by invitation (SIM-0491, SIM-0492, SIM-0766). The pure-model
/// half: native <c>CFSMission::AddInvitation</c> (fsmission.cpp:1168-1174),
/// <c>RequiresInvitation() == (nInvitationListID != 0)</c> (fsmission.h:407-411),
/// <c>CFSSide::IsInvited</c> (fsside.cpp:34-64) and <c>CFSMission::IsInvited</c> (any
/// side, fsmission.cpp:1182-1192), plus the native "list not yet loaded" window where a
/// <see cref="Mission.RequiresInvitation"/> mission has empty lists and admits nobody.
/// Assembled from real artwork (core + map) so the per-side bounds and iteration are real.
/// Each test fails before the change (the members did not exist / no gate to observe).
/// </summary>
public sealed class P854PrivateGameInvitationModelTests
{
    [SkippableFact]
    public void PublicGame_does_not_require_invitation_and_invites_nobody()
    {
        Require.Artwork();
        var m = NewMission();
        Assert.False(m.RequiresInvitation);                 // no lists configured → public
        Assert.False(m.IsSideInvited(0, "Alice", []));      // nothing loaded, no gate fires
        Assert.False(m.IsSideInvited(1, "Alice", []));
        Assert.False(m.IsInvited("Alice", []));
    }

    [SkippableFact]
    public void Configured_invitations_flag_the_mission_and_load_per_side()
    {
        Require.Artwork();
        var m = NewMission();
        m.RequiresInvitation = true;
        m.LoadInvitations(new Dictionary<short, IReadOnlyList<string>>
        {
            [0] = new[] { "Alice", "Bob" },
            [1] = new[] { "Carol" },
        });

        Assert.True(m.RequiresInvitation);
        Assert.Equal(new[] { "Alice", "Bob" }, m.SideInvitations[0]);
        Assert.Single(m.SideInvitations[1]);
        Assert.True(m.IsSideInvited(0, "Alice", []));       // invited to side 0
        Assert.True(m.IsSideInvited(0, "Bob", []));
        Assert.True(m.IsSideInvited(1, "Carol", []));       // invited to side 1
        Assert.True(m.IsInvited("Alice", []));              // any side
        Assert.True(m.IsInvited("Carol", []));
    }

    [SkippableFact]
    public void Uninvited_callsign_is_not_invited_to_any_side()
    {
        Require.Artwork();
        var m = NewMission();
        m.RequiresInvitation = true;
        m.LoadInvitations(new Dictionary<short, IReadOnlyList<string>>
        {
            [0] = new[] { "Alice" },
            [1] = new[] { "Bob" },
        });

        Assert.False(m.IsSideInvited(0, "Mallory", []));
        Assert.False(m.IsSideInvited(1, "Mallory", []));
        Assert.False(m.IsInvited("Mallory", []));           // the mission-join gate refuses this player
    }

    [SkippableFact]
    public void PerSide_gate_only_allows_the_side_that_invited_you()
    {
        Require.Artwork();
        var m = NewMission();
        m.RequiresInvitation = true;
        m.LoadInvitations(new Dictionary<short, IReadOnlyList<string>>
        {
            [0] = new[] { "Alice" },
            [1] = new[] { "Bob" },
        });

        // Alice is invited ONLY to side 0: the per-side gate (fsmission.cpp:4605)
        // admits side 0 but refuses side 1 with DPR_PrivateGame.
        Assert.True(m.IsSideInvited(0, "Alice", []));
        Assert.False(m.IsSideInvited(1, "Alice", []));
        // Bob is invited ONLY to side 1: the mirror image.
        Assert.False(m.IsSideInvited(0, "Bob", []));
        Assert.True(m.IsSideInvited(1, "Bob", []));
    }

    [SkippableFact]
    public void Matching_is_case_insensitive_like_native_stricmp()
    {
        Require.Artwork();
        var m = NewMission();
        m.RequiresInvitation = true;
        m.LoadInvitations(new Dictionary<short, IReadOnlyList<string>>
        {
            [0] = new[] { "Alice" },
        });

        Assert.True(m.IsSideInvited(0, "aLiCe", []));       // fsside.cpp:55 _stricmp
        Assert.True(m.IsSideInvited(0, "ALICE", []));
        Assert.False(m.IsSideInvited(0, "alice!" , []));    // not a substring — exact, case-insensitive
    }

    [SkippableFact]
    public void SquadGame_matches_squad_names_not_callsigns()
    {
        Require.Artwork();
        var m = NewMission();
        m.SquadGame = true;                                  // native bSquadGame (igc.h:1142)
        m.RequiresInvitation = true;
        m.LoadInvitations(new Dictionary<short, IReadOnlyList<string>>
        {
            [0] = new[] { "Squad A" },                       // a SQUAD name, not a callsign
        });

        // A member of Squad A is invited to side 0 (fsside.cpp:40-51).
        Assert.True(m.IsSideInvited(0, "Some Pilot", new[] { "squad a" }));
        // The callsign never matches in a squad game — only squad memberships do.
        Assert.False(m.IsSideInvited(0, "Squad A", []));
        // A member of a different squad is not invited.
        Assert.False(m.IsSideInvited(0, "Some Pilot", new[] { "Squad B" }));
    }

    [SkippableFact]
    public void ListNotYetLoaded_refuses_everyone_until_the_lists_arrive()
    {
        Require.Artwork();
        var m = NewMission();
        // Native marks the mission RequiresInvitation at construction
        // (m_nInvitationListID set, fsmission.cpp:217) but the per-side lists arrive
        // ASYNCHRONOUSLY (GetInvitations → GotInvitationList, FedSrv.CPP:1307-1318,
        // 785-801). In that window the lists are empty, so IsInvited sees nothing and
        // EVERY join is refused — the mission-join and per-side gates refuse with
        // DPR_PrivateGame.
        m.RequiresInvitation = true;                         // lists NOT yet loaded
        Assert.True(m.RequiresInvitation);
        Assert.False(m.IsSideInvited(0, "Alice", []));
        Assert.False(m.IsInvited("Alice", []));              // nobody admitted pre-load

        m.LoadInvitations(new Dictionary<short, IReadOnlyList<string>>
        {
            [0] = new[] { "Alice" },
        });
        Assert.True(m.IsSideInvited(0, "Alice", []));        // once loaded, Alice is in
    }

    private static Mission NewMission()
    {
        var corePath = Corpus.Files.First(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase));
        var core = StaticCore.Load(IgcReader.ParseFile(corePath));
        var mapPath = Corpus.Files.First(p => Path.GetFileName(p).Equals("XXV_simple.igc", StringComparison.OrdinalIgnoreCase));
        var map = MissionMap.Load(IgcReader.ParseFile(mapPath));
        var civs = core.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(2).Select(c => c.CivilizationId).ToArray();
        return Mission.Assemble(core, map,
            [new SideSpec(0, "Alpha", civs[0]), new SideSpec(1, "Bravo", civs[1])],
            new NullIgcSite());
    }
}

/// <summary>
/// P854 — the server half: the per-side join gate (<c>CheckPositionRequest</c>,
/// native fsmission.cpp:4605 → DPR_PrivateGame) and the mission-join gate
/// (native FedSrv.CPP:581-594 → DPR_PrivateGame, iSide = NA) wired through a booted
/// headless server with config-driven invitation lists, seeded peers and verified
/// identities. Each test fails before the change (the gate returned no refusal).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P854PrivateGameInvitationServerGateTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(Dictionary<short, List<string>>? invitations = null, bool squadGame = false) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm
            {
                Name = "P854 Private", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
                SideNames = ["Alpha", "Bravo"],
                SquadGame = squadGame,
                SideInvitations = invitations ?? new(),
            },
        ],
    };

    private static JoinIdentity Identity(params SquadClaim[] squads) => new(
        Iss: "https://lobby.test", Aud: "game", Sub: "00000000000000000000000000000001", Callsign: "Pilot",
        Iat: 1700000000, Exp: 1700086400, Jti: "11111111111111111111111111111111",
        squads.Length == 0 ? null : squads);

    private static Ship Seat(Mission mission, short id, short side)
    {
        var s = new Ship(id, mission.Core.HullTypes.Values.First(), new Vector3(0, 10000, 0),
            Vector3.Zero, Orientation.Identity) { SideId = side };
        mission.PlaceShip(s, mission.Sectors[0]);
        return s;
    }

    /// <summary>Seed a private (callsign) game inviting only "seed-700" (peer 700's
    /// seeded callsign, TestSeedPeers → "seed-&lt;conn&gt;") to side 0, with peer 700
    /// seated on side 0 and peer 701 (invited nowhere) on side 1.</summary>
    private static (Srv Server, Mission Mission, Ship Invited, Ship Stranger) BootCallsignPrivate()
    {
        var server = new Srv();
        server.Boot(Config(new Dictionary<short, List<string>> { [0] = new() { "seed-700" } }));
        var mission = server.TestMission!;
        var invited = Seat(mission, 19000, 0);
        var stranger = Seat(mission, 19001, 1);
        server.TestSeedPeers([(new ConnectionId(700), invited, mission.Sectors[0]),
                              (new ConnectionId(701), stranger, mission.Sectors[0])]);
        return (server, mission, invited, stranger);
    }

    [SkippableFact]
    public void PerSideGate_allows_invited_side_and_refuses_uninvited_with_PrivateGame()
    {
        Require.Artwork();
        var (server, mission, invited, stranger) = BootCallsignPrivate();
        using var _ = server;

        Assert.True(mission.RequiresInvitation);            // config → assemble wiring
        // invited is "seed-700", invited to side 0.
        Assert.Null(server.TestCheckPositionRequest(mission, 0, invited));        // invited side → admitted
        Assert.Equal(DelPositionReqReason.PrivateGame, server.TestCheckPositionRequest(mission, 1, invited)); // per-side gate → refused
        // stranger ("seed-701") is invited to NO side: refused everywhere (the
        // mission-join gate already stops such a player before side selection).
        Assert.Equal(DelPositionReqReason.PrivateGame, server.TestCheckPositionRequest(mission, 0, stranger));
        Assert.Equal(DelPositionReqReason.PrivateGame, server.TestCheckPositionRequest(mission, 1, stranger));
    }

    [SkippableFact]
    public void PerSideGate_squad_mode_matches_identity_squads()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(new Dictionary<short, List<string>> { [0] = new() { "Squad A" } }, squadGame: true));
        var mission = server.TestMission!;
        var member = Seat(mission, 19100, 0);
        var outsider = Seat(mission, 19101, 0);
        server.TestSeedPeers([(new ConnectionId(800), member, mission.Sectors[0]),
                              (new ConnectionId(801), outsider, mission.Sectors[0])]);
        server.TestSetIdentity(new ConnectionId(800), Identity(new SquadClaim("aaaa", "Squad A", true)));
        server.TestSetIdentity(new ConnectionId(801), Identity(new SquadClaim("bbbb", "Squad B", true)));

        Assert.True(mission.RequiresInvitation);
        Assert.Null(server.TestCheckPositionRequest(mission, 0, member));                  // on Squad A → admitted
        Assert.Equal(DelPositionReqReason.PrivateGame, server.TestCheckPositionRequest(mission, 0, outsider)); // Squad B → refused
    }

    [SkippableFact]
    public void MissionJoinGate_admits_invited_to_any_side_and_refuses_the_rest()
    {
        Require.Artwork();
        var (server, mission, _, _) = BootCallsignPrivate();
        using var _ = server;

        // Native mission-join gate (FedSrv.CPP:581-594): invited to at least one side → in.
        Assert.True(server.TestAdmittedAtMissionJoin(new ConnectionId(700), "seed-700", mission));
        // The callsign match is name-based, not connection-based.
        Assert.True(server.TestAdmittedAtMissionJoin(new ConnectionId(702), "seed-700", mission));
        // A player invited to NO side is refused outright (never reaches side selection).
        Assert.False(server.TestAdmittedAtMissionJoin(new ConnectionId(702), "Mallory", mission));
    }
}
