using System.Threading;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P818 — pure-logic tests for the composer's individual/group recipient
/// selection: native's <c>ChatSubject</c> ship list and its three consumers (the
/// wire plan, the compose label, the radar's <c>c_maskSubject</c> test). Every
/// rule cites the native site it ports; none of this needs a graphics device.
/// </summary>
public class P818ChatSubjectsTests
{
    private const short Self = 7;

    // ── ChatSubject: SetRecipient / ToggleSubject / Reset ───────────────────

    [Fact]
    public void Fresh_recipient_is_team_with_no_subjects()
    {
        // console.cpp:644 — m_chsChat(CHAT_TEAM); m_shipsSubject starts empty.
        var subject = new ChatSubjects();
        Assert.Equal(ChatTarget.Team, subject.Target);
        Assert.Empty(subject.Ships);
        Assert.Null(subject.IndividualShipId);
    }

    [Fact]
    public void SetIndividual_tracks_the_picked_ship()
    {
        // console.cpp:299-309 — SetRecipient(CHAT_INDIVIDUAL, oid, pship).
        var subject = new ChatSubjects();
        subject.SetIndividual(11, Self);
        Assert.Equal(ChatTarget.Individual, subject.Target);
        Assert.Equal(new short[] { 11 }, subject.Ships);
        Assert.Equal((short)11, subject.IndividualShipId);
        Assert.True(subject.Contains(11));
        Assert.False(subject.Contains(12));
    }

    [Fact]
    public void SetIndividual_of_a_ship_that_left_the_mission_becomes_team()
    {
        // console.cpp:301-308 — no mission → CHAT_TEAM.
        var subject = new ChatSubjects();
        subject.SetIndividual(11, Self, inMission: _ => false);
        Assert.Equal(ChatTarget.Team, subject.Target);
        Assert.Empty(subject.Ships);
    }

    [Fact]
    public void SetGroup_decays_exactly_like_native()
    {
        // console.cpp:278-298 — one survivor → Individual, none → Team.
        var many = new ChatSubjects();
        many.SetGroup([11, 12, 13], Self, inMission: _ => true);
        Assert.Equal(ChatTarget.Group, many.Target);
        Assert.Equal(new short[] { 11, 12, 13 }, many.Ships);

        var one = new ChatSubjects();
        one.SetGroup([11], Self, inMission: _ => true);
        Assert.Equal(ChatTarget.Individual, one.Target);
        Assert.Equal((short)11, one.IndividualShipId);

        var none = new ChatSubjects();
        none.SetGroup([11], Self, inMission: _ => false);
        Assert.Equal(ChatTarget.Team, none.Target);
        Assert.Empty(none.Ships);
    }

    [Fact]
    public void AddSubject_puts_the_players_own_ship_first()
    {
        // console.cpp:104-109 — the client's ship goes to the FRONT of the list
        // "so that it will correctly echo the chat in the chat history".
        var subject = new ChatSubjects();
        subject.SetGroup([11, Self, 12], Self, inMission: _ => true);
        Assert.Equal(new short[] { Self, 11, 12 }, subject.Ships);
        Assert.Equal(ChatTarget.Group, subject.Target);
    }

    [Fact]
    public void ToggleSubject_adds_removes_and_never_drops_the_last_ship()
    {
        // console.cpp:123-158 — shift-click toggle, Individual ↔ Group.
        var subject = new ChatSubjects();
        subject.SetIndividual(11, Self);
        subject.ToggleSubject(12, Self);
        Assert.Equal(ChatTarget.Group, subject.Target);
        Assert.Equal(new short[] { 11, 12 }, subject.Ships);

        subject.ToggleSubject(11, Self);
        Assert.Equal(ChatTarget.Individual, subject.Target);
        Assert.Equal(new short[] { 12 }, subject.Ships);
        Assert.Equal((short)12, subject.IndividualShipId);

        // "Do not allow the last person to be toggled out of the group"
        // (console.cpp:128-133).
        subject.ToggleSubject(12, Self);
        Assert.Equal(ChatTarget.Individual, subject.Target);
        Assert.Equal(new short[] { 12 }, subject.Ships);

        // Toggling the player's own ship in puts it first (console.cpp:138-141).
        subject.ToggleSubject(Self, Self);
        Assert.Equal(ChatTarget.Group, subject.Target);
        Assert.Equal(new short[] { Self, 12 }, subject.Ships);
    }

    [Fact]
    public void Reset_drops_ships_that_left_the_mission()
    {
        // console.cpp:229-251 — a group's dead members are purged before the send
        // (SendChatInternal calls Reset, :1453).
        var subject = new ChatSubjects();
        subject.SetGroup([11, 12, 13], Self, inMission: _ => true);
        subject.Reset(starting: false, Self, inMission: id => id != 12);
        Assert.Equal(new short[] { 11, 13 }, subject.Ships);
        Assert.Equal(ChatTarget.Group, subject.Target);

        subject.Reset(starting: false, Self, inMission: id => id == 13);
        Assert.Equal(new short[] { 13 }, subject.Ships);
        Assert.Equal(ChatTarget.Individual, subject.Target);

        subject.Reset(starting: false, Self, inMission: _ => false);
        Assert.Equal(ChatTarget.Team, subject.Target);
        Assert.Empty(subject.Ships);
    }

    [Fact]
    public void Reset_on_compose_start_turns_self_and_drone_individuals_into_team()
    {
        // console.cpp:255-266 — "Chats to a drone or myself convert to the team".
        var self = new ChatSubjects();
        self.SetIndividual(Self, Self);
        self.Reset(starting: true, Self, inMission: _ => true, playerPiloted: _ => true);
        Assert.Equal(ChatTarget.Team, self.Target);
        Assert.Empty(self.Ships);

        // A drone (pilot type below c_ptPlayer) converts too.
        var drone = new ChatSubjects();
        drone.SetIndividual(21, Self);
        drone.Reset(starting: true, Self, inMission: _ => true, playerPiloted: _ => false);
        Assert.Equal(ChatTarget.Team, drone.Target);
        Assert.Empty(drone.Ships);

        // Another PLAYER survives the compose start unchanged.
        var other = new ChatSubjects();
        other.SetIndividual(11, Self);
        other.Reset(starting: true, Self, inMission: _ => true, playerPiloted: _ => true);
        Assert.Equal(ChatTarget.Individual, other.Target);
        Assert.Equal(new short[] { 11 }, other.Ships);
    }

    [Fact]
    public void SetChannel_purges_the_ship_list()
    {
        // console.cpp:274 — every SetRecipient starts with PurgeSubjects, so a
        // channel recipient carries no individual/group subjects.
        var subject = new ChatSubjects();
        subject.SetIndividual(11, Self);
        subject.SetChannel(ChatTarget.Team);
        Assert.Equal(ChatTarget.Team, subject.Target);
        Assert.Empty(subject.Ships);
    }

    // ── what a picked recipient sends (SendChatInternal) ────────────────────

    [Fact]
    public void Group_sends_one_chat_per_subject_first_echoing_then_noecho()
    {
        // console.cpp:1470-1491 — first CHAT_GROUP, the rest CHAT_GROUP_NOECHO;
        // clintlib.cpp:2463-2470 echoes every send except CHAT_GROUP_NOECHO.
        var plan = ChatSendPlan.Plan(ChatTarget.Group, [11, 12, 13], oidRecipient: 0, selfId: Self);
        Assert.Equal(3, plan.Count);
        Assert.Equal((ChatTarget.Group, (short)11), (plan[0].Target, plan[0].OidRecipient));
        Assert.True(plan[0].EchoLocally);
        Assert.False(plan[0].LocalOnly);
        Assert.Equal((ChatTarget.GroupNoEcho, (short)12), (plan[1].Target, plan[1].OidRecipient));
        Assert.False(plan[1].EchoLocally);
        Assert.Equal((ChatTarget.GroupNoEcho, (short)13), (plan[2].Target, plan[2].OidRecipient));
        Assert.False(plan[2].EchoLocally);
        Assert.All(plan, s => Assert.False(s.LocalOnly));
    }

    [Fact]
    public void Group_that_includes_the_player_relies_on_the_relayed_self_copy()
    {
        // The sender's own ship leads the list (console.cpp:104-109); the server
        // relays that copy back, so the composer must not also echo locally or
        // the player would see the line twice.
        var plan = ChatSendPlan.Plan(ChatTarget.Group, [Self, 12], oidRecipient: 0, selfId: Self);
        Assert.Equal(2, plan.Count);
        Assert.Equal((ChatTarget.Group, Self), (plan[0].Target, plan[0].OidRecipient));
        Assert.False(plan[0].EchoLocally);
        Assert.False(plan[1].EchoLocally);
    }

    [Fact]
    public void Individual_sends_one_chat_to_the_picked_ship()
    {
        var plan = ChatSendPlan.Plan(ChatTarget.Individual, [11], oidRecipient: 11, selfId: Self);
        var send = Assert.Single(plan);
        Assert.Equal(ChatTarget.Individual, send.Target);
        Assert.Equal((short)11, send.OidRecipient);
        Assert.False(send.LocalOnly);
        Assert.True(send.EchoLocally);
    }

    [Fact]
    public void Individual_to_yourself_is_local_only_with_one_echo()
    {
        // clintlib.cpp:2422-2426 — an Individual chat to your own ship is never
        // put on the wire; "We always get chats we send" (clintlib.cpp:2463).
        var plan = ChatSendPlan.Plan(ChatTarget.Individual, [Self], oidRecipient: Self, selfId: Self);
        var send = Assert.Single(plan);
        Assert.True(send.LocalOnly);
        Assert.True(send.EchoLocally);
    }

    [Fact]
    public void Channel_targets_send_once_with_no_local_echo()
    {
        // The port's server relays the sender's own copy for every broadcast
        // channel, so the composer must not duplicate it.
        foreach (var target in new[] { ChatTarget.Team, ChatTarget.Everyone, ChatTarget.Wing, ChatTarget.FriendlySector })
        {
            var plan = ChatSendPlan.Plan(target, [], oidRecipient: 3, selfId: Self);
            var send = Assert.Single(plan);
            Assert.Equal(target, send.Target);
            Assert.False(send.LocalOnly);
            Assert.False(send.EchoLocally);
        }
    }

    // ── the radar's c_maskSubject test ─────────────────────────────────────

    [Fact]
    public void Individual_and_group_subjects_are_pure_ship_list_membership()
    {
        // radarimage.cpp:651 — c_maskSubject iff psubjects->find(pship), i.e.
        // membership in the compose's list; no side/sector condition.
        short[] subjects = [11, 12];
        Assert.True(RadarOverlayRules.IsSubject(ChatTarget.Individual, true, sameSide: true, sameSector: true,
            shipWingId: 0, myWingId: 0, shipId: 11, subjectShips: subjects));
        Assert.True(RadarOverlayRules.IsSubject(ChatTarget.Group, true, sameSide: false, sameSector: false,
            shipWingId: 0, myWingId: 0, shipId: 12, subjectShips: subjects));
        Assert.False(RadarOverlayRules.IsSubject(ChatTarget.Group, true, sameSide: true, sameSector: true,
            shipWingId: 0, myWingId: 0, shipId: 13, subjectShips: subjects));
        // No list (or not composing) → no fabricated subject.
        Assert.False(RadarOverlayRules.IsSubject(ChatTarget.Individual, true, true, true, 0, 0, shipId: 11));
        Assert.False(RadarOverlayRules.IsSubject(ChatTarget.Group, composing: false, true, true, 0, 0,
            shipId: 11, subjectShips: subjects));
        // Still gated to the command camera (psubjects is NULL with range labels,
        // radarimage.cpp:570-574).
        Assert.False(RadarOverlayRules.SubjectsApply(commandCamera: false, composing: true));
    }

    // ── the compose label ──────────────────────────────────────────────────

    [Fact]
    public void Compose_label_shows_the_picked_ship_and_the_group_tag()
    {
        // console.cpp:1345-1346 ("Group") and 1381-1386 (the individual ship's
        // name) — the recipient box's two ship-list cases.
        Assert.Equal("[GROUP] ", ChatTargetLogic.ResolvedLabel(ChatTarget.Group, null, null, null, null));
        Assert.Equal("[GROUP] ", ChatTargetLogic.ComposeLabel(ChatTarget.Group));
        Assert.Equal("[pilot-bob] ", ChatTargetLogic.ResolvedLabel(ChatTarget.Individual, null, null, null, "pilot-bob"));
        Assert.Equal("[ME] ", ChatTargetLogic.ResolvedLabel(ChatTarget.Individual, null, null, null, null));
    }
}

/// <summary>
/// P818 — the picked recipients on the REAL wire, over a real TCP loopback
/// transport (the P757 production-seam idiom): the composer's send loop puts one
/// <c>CHATMESSAGE(23)</c> per group subject on the wire — <c>CHAT_GROUP</c> first,
/// <c>CHAT_GROUP_NOECHO</c> after (console.cpp:1470-1491) — and keeps exactly one
/// copy of the line in the sender's own history (the local echo native's
/// <c>ReceiveChat</c> performs, clintlib.cpp:2463-2470).
/// </summary>
public class P818ChatSubjectsWireTests
{
    private const System.Reflection.BindingFlags Hidden =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    private static void Set(Game1 game, string name, object value) =>
        typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private static object? Call(Game1 game, string name, params object?[] args) =>
        typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);

    private sealed class Site : Allegiance.Core.Transport.ITransportSite
    {
        public readonly TaskCompletionSource<Allegiance.Core.Transport.ConnectionId> Joined =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> Received = new();
        public void OnPeerJoined(Allegiance.Core.Transport.ConnectionId id, string name) => Joined.TrySetResult(id);
        public void OnPeerLeft(Allegiance.Core.Transport.ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(Allegiance.Core.Transport.ConnectionId id, ReadOnlySpan<byte> bytes)
        { foreach (var message in Allegiance.Core.Wire.FedPacket.Unpack(bytes)) Received.Enqueue(message); }
    }

    private static async Task Until(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!done()) await Task.Delay(5, timeout.Token);
    }

    private static Game1 Headless(Allegiance.ClientCore.Session.ConnectionSession session)
    {
        var game = (Game1)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_session", session);
        Set(game, "_moderation", new ModerationModel());
        Set(game, "_chatPane", new ChatPaneModel());
        Set(game, "_chatBuffer", new System.Text.StringBuilder());
        Set(game, "_chatRecipient", new ChatSubjects());
        Set(game, "_pilotProfile", new PilotInfoLayout.ProfileEditor());
        var state = typeof(Game1).GetField("_state", Hidden)!;
        state.SetValue(game, Enum.Parse(state.FieldType, "Flying"));
        return game;
    }

    /// <summary>⭐ A group recipient goes out as one chat per subject.</summary>
    [Fact]
    public async Task Group_recipient_sends_one_chat_per_subject_on_the_real_wire()
    {
        var site = new Site();
        var host = new Allegiance.Core.Transport.TcpTransport(site);
        using var session = new Allegiance.ClientCore.Session.ConnectionSession();
        host.Host(0);
        session.Connect(host.BoundPort, "P818 group chat");
        await site.Joined.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The two subjects exist in the world (native GetMission() != NULL), so
        // the pre-send Reset keeps them (console.cpp:1453).
        session.World.Apply(new Allegiance.Core.Wire.ExportShip { ShipId = 11, SideId = 0, CharacterName = "pilot-a" });
        session.World.Apply(new Allegiance.Core.Wire.ExportShip { ShipId = 12, SideId = 0, CharacterName = "pilot-b" });

        var game = Headless(session);
        var recipient = (ChatSubjects)typeof(Game1).GetField("_chatRecipient", Hidden)!.GetValue(game)!;
        recipient.SetGroup([11, 12], selfId: -1);
        Assert.Equal(ChatTarget.Group, recipient.Target);

        Set(game, "_chatBuffer", new System.Text.StringBuilder("regroup"));
        Call(game, "SendChatLine");

        var sent = new List<Allegiance.Core.Wire.ChatMessage>();
        await Until(() =>
        {
            while (site.Received.TryDequeue(out var m))
                if (Allegiance.Core.Wire.FedMessageHeader.Read(m).Fmid == Allegiance.Core.Wire.ChatMessage.Fmid)
                    sent.Add(Allegiance.Core.Wire.ChatMessage.Decode(m));
            return sent.Count >= 2;
        });
        Assert.Equal(2, sent.Count);
        Assert.Equal((int)ChatTarget.Group, sent[0].ChatTarget);
        Assert.Equal((short)11, sent[0].OidRecipient);
        Assert.Equal((int)ChatTarget.GroupNoEcho, sent[1].ChatTarget);
        Assert.Equal((short)12, sent[1].OidRecipient);
        Assert.All(sent, m => Assert.Equal(Allegiance.Core.Wire.OrderChange.CidNone, m.CommandID));
        // The sender's own line: exactly one local echo (native ReceiveChat), and
        // the server never sends a Group line back to the sender.
        Assert.Single(session.World.Events.Chats);
        Assert.Equal("regroup", session.World.Events.Chats[0].Message);
    }

    /// <summary>An individual recipient is one chat addressed to the picked
    /// ship (not the player's own).</summary>
    [Fact]
    public async Task Individual_recipient_sends_to_the_picked_ship()
    {
        var site = new Site();
        var host = new Allegiance.Core.Transport.TcpTransport(site);
        using var session = new Allegiance.ClientCore.Session.ConnectionSession();
        host.Host(0);
        session.Connect(host.BoundPort, "P818 individual chat");
        await site.Joined.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.World.Apply(new Allegiance.Core.Wire.ExportShip { ShipId = 11, SideId = 0, CharacterName = "pilot-a" });

        var game = Headless(session);
        var recipient = (ChatSubjects)typeof(Game1).GetField("_chatRecipient", Hidden)!.GetValue(game)!;
        recipient.SetIndividual(11, selfId: -1);

        Set(game, "_chatBuffer", new System.Text.StringBuilder("on me"));
        Call(game, "SendChatLine");

        Allegiance.Core.Wire.ChatMessage? sent = null;
        await Until(() =>
        {
            while (site.Received.TryDequeue(out var m))
                if (Allegiance.Core.Wire.FedMessageHeader.Read(m).Fmid == Allegiance.Core.Wire.ChatMessage.Fmid)
                    sent = Allegiance.Core.Wire.ChatMessage.Decode(m);
            return sent is not null;
        });
        Assert.NotNull(sent);
        Assert.Equal((int)ChatTarget.Individual, sent!.ChatTarget);
        Assert.Equal((short)11, sent.OidRecipient);
        Assert.Equal("on me", sent.Message);
        Assert.Single(session.World.Events.Chats);
    }

    [Fact]
    public void Radar_bracket_quadrants_are_centred_on_the_contact()
    {
        // Native DrawExpandedBlip centres each quadrant on (±delta, ±delta)
        // (radarimage.cpp:273-276): the four quadrants' bounding box is centred on
        // the contact. (The port used to place each top-left there: down-right shift.)
        var centre = new Microsoft.Xna.Framework.Vector2(400, 300);
        const int w = 32, h = 32; const float delta = 20;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int q = 0; q < 4; q++)
        {
            var src = RadarBracketRenderer.ExpandedBlipQuadrant(q, w, h);
            var tl = RadarBracketRenderer.ExpandedBlipQuadrantTopLeft(q, src, centre, delta);
            minX = Math.Min(minX, tl.X); minY = Math.Min(minY, tl.Y);
            maxX = Math.Max(maxX, tl.X + src.Width); maxY = Math.Max(maxY, tl.Y + src.Height);
        }
        Assert.Equal(centre.X, (minX + maxX) / 2f, 3);
        Assert.Equal(centre.Y, (minY + maxY) / 2f, 3);
    }
}
