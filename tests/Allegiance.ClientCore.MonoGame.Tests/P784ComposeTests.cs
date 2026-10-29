using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Xunit;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P784 — chat and command composition: the missing compose paths, per-recipient
/// order fan-out and leader-bold history.
///
/// <list type="bullet">
/// <item><b>ComposeMachine</b> — the pure port of native's compose state machine
/// (console.cpp:524-529 and the transition sites cited per case) driven through
/// every mode transition.</item>
/// <item><b>ShellCommands</b> — the <c>!</c> shell compose's parse
/// (console.cpp:2388-2396 entry; clintlib.cpp:2277-2311 commands).</item>
/// <item><b>CommandFanout</b> — per-recipient order resolution
/// (native SendChatInternal's CHAT_GROUP subject loop, console.cpp:1546-1564;
/// the CHAT_WING subject sweep, console.cpp:398-406), plus a loopback wire test
/// asserting ONE order CHATMESSAGE(23) per recipient.</item>
/// <item><b>Leader-bold</b> — the chat history's per-line leader flag
/// (native trekigc.cpp:4498 + chatpane.cpp:34,93 bold face).</item>
/// <item><b>Compose flow</b> — the production handler's new transitions
/// ('!' → Shell, Enter-in-Shell runs the shell command, Escape Command→Chat)
/// driven headlessly over a real <see cref="ConnectionSession"/> world.</item>
/// </list>
/// </summary>
public class P784ComposeTests
{
    // ── 1. The compose state machine ─────────────────────────────────────────

    [Fact]
    public void StartChat_opens_Chat_from_every_state()
    {
        // console.cpp:2423-2431 — StartComposing(Chat) from NotComposing,
        // SetComposeState(Chat) from any composing state.
        foreach (NativeComposeState state in Enum.GetValues<NativeComposeState>())
            Assert.Equal(NativeComposeState.Chat, ComposeMachine.Next(state, ComposeMachine.Trigger.StartChat));
    }

    [Fact]
    public void VerbButton_moves_to_Command_unless_Shell()
    {
        // console.cpp:1264-1279 — Shell swallows the press (:1266); every other
        // state starts/moves to Command (:1270-1273).
        foreach (NativeComposeState state in Enum.GetValues<NativeComposeState>())
        {
            var expected = state == NativeComposeState.Shell ? NativeComposeState.Shell : NativeComposeState.Command;
            Assert.Equal(expected, ComposeMachine.Next(state, ComposeMachine.Trigger.VerbButton));
        }
    }

    [Fact]
    public void RecipientButton_starts_Chat_only_from_NotComposing()
    {
        // console.cpp:1134-1192 — Shell swallows (:1136); NotComposing starts a
        // chat compose (:1185-1186); a composing state only retargets.
        Assert.Equal(NativeComposeState.Chat, ComposeMachine.Next(NativeComposeState.NotComposing, ComposeMachine.Trigger.RecipientButton));
        Assert.Equal(NativeComposeState.Shell, ComposeMachine.Next(NativeComposeState.Shell, ComposeMachine.Trigger.RecipientButton));
        Assert.Equal(NativeComposeState.Chat, ComposeMachine.Next(NativeComposeState.Chat, ComposeMachine.Trigger.RecipientButton));
        Assert.Equal(NativeComposeState.Command, ComposeMachine.Next(NativeComposeState.Command, ComposeMachine.Trigger.RecipientButton));
        Assert.Equal(NativeComposeState.MouseCommand, ComposeMachine.Next(NativeComposeState.MouseCommand, ComposeMachine.Trigger.RecipientButton));
        Assert.Equal(NativeComposeState.Quick, ComposeMachine.Next(NativeComposeState.Quick, ComposeMachine.Trigger.RecipientButton));
    }

    [Fact]
    public void Mouse_picks_start_MouseCommand_only_from_NotComposing()
    {
        // console.cpp:1776-1790 (PickShip) and :2128-2131 (drag rectangle).
        foreach (var trigger in new[] { ComposeMachine.Trigger.PickShip, ComposeMachine.Trigger.RectangleGroup })
        {
            Assert.Equal(NativeComposeState.MouseCommand,
                ComposeMachine.Next(NativeComposeState.NotComposing, trigger));
            Assert.Equal(NativeComposeState.Chat, ComposeMachine.Next(NativeComposeState.Chat, trigger));
            Assert.Equal(NativeComposeState.Command, ComposeMachine.Next(NativeComposeState.Command, trigger));
        }
    }

    [Fact]
    public void OpenQuickChat_moves_every_state_to_Quick()
    {
        // console.cpp:1508-1532 — StartQuickChat from NotComposing, or
        // SetComposeState(Quick) while composing.
        foreach (NativeComposeState state in Enum.GetValues<NativeComposeState>())
            Assert.Equal(NativeComposeState.Quick, ComposeMachine.Next(state, ComposeMachine.Trigger.OpenQuickChat));
    }

    [Fact]
    public void TypeBang_enters_Shell_only_from_Chat_with_an_empty_buffer()
    {
        // console.cpp:2388-2396 — '!' while composing CHAT with an EMPTY buffer.
        Assert.Equal(NativeComposeState.Shell,
            ComposeMachine.Next(NativeComposeState.Chat, ComposeMachine.Trigger.TypeBang, bufferEmpty: true));
        Assert.Equal(NativeComposeState.Chat,
            ComposeMachine.Next(NativeComposeState.Chat, ComposeMachine.Trigger.TypeBang, bufferEmpty: false));
        Assert.Equal(NativeComposeState.Command,
            ComposeMachine.Next(NativeComposeState.Command, ComposeMachine.Trigger.TypeBang, bufferEmpty: true));
        Assert.Equal(NativeComposeState.NotComposing,
            ComposeMachine.Next(NativeComposeState.NotComposing, ComposeMachine.Trigger.TypeBang, bufferEmpty: true));
    }

    [Fact]
    public void Escape_steps_Command_back_to_Chat_and_quits_everything_else()
    {
        // console.cpp:2375-2385 — Command → Chat (text cleared); all others quit.
        Assert.Equal(NativeComposeState.Chat, ComposeMachine.Next(NativeComposeState.Command, ComposeMachine.Trigger.Escape));
        Assert.Equal(NativeComposeState.NotComposing, ComposeMachine.Next(NativeComposeState.Chat, ComposeMachine.Trigger.Escape));
        Assert.Equal(NativeComposeState.NotComposing, ComposeMachine.Next(NativeComposeState.Shell, ComposeMachine.Trigger.Escape));
        Assert.Equal(NativeComposeState.NotComposing, ComposeMachine.Next(NativeComposeState.MouseCommand, ComposeMachine.Trigger.Escape));
        Assert.Equal(NativeComposeState.NotComposing, ComposeMachine.Next(NativeComposeState.Quick, ComposeMachine.Trigger.Escape));
    }

    [Theory]
    [InlineData(ComposeMachine.Trigger.EnterEmpty)]   // console.cpp:2360-2363
    [InlineData(ComposeMachine.Trigger.Send)]         // SendChat → QuitComposing, console.cpp:1618-1620
    [InlineData(ComposeMachine.Trigger.Backquote)]    // console.cpp:2386-2387
    public void Quitting_triggers_land_in_NotComposing_from_every_state(ComposeMachine.Trigger trigger)
    {
        foreach (NativeComposeState state in Enum.GetValues<NativeComposeState>())
            Assert.Equal(NativeComposeState.NotComposing, ComposeMachine.Next(state, trigger));
    }

    [Fact]
    public void SwitchViewMode_quits_only_MouseCommand()
    {
        // console.cpp:1031-1035.
        Assert.Equal(NativeComposeState.NotComposing,
            ComposeMachine.Next(NativeComposeState.MouseCommand, ComposeMachine.Trigger.SwitchViewMode));
        Assert.Equal(NativeComposeState.Chat,
            ComposeMachine.Next(NativeComposeState.Chat, ComposeMachine.Trigger.SwitchViewMode));
        Assert.Equal(NativeComposeState.Command,
            ComposeMachine.Next(NativeComposeState.Command, ComposeMachine.Trigger.SwitchViewMode));
    }

    [Fact]
    public void CtrlVerb_moves_a_keystroke_taking_empty_buffered_state_to_Command()
    {
        // console.cpp:2343-2357 — Ctrl+<verb letter> requires OnChar's
        // keystroke gate (state > MouseCommand, :2335-2338), a non-Command
        // state and an empty buffer.
        Assert.Equal(NativeComposeState.Command, ComposeMachine.Next(NativeComposeState.Chat, ComposeMachine.Trigger.CtrlVerb, bufferEmpty: true));
        Assert.Equal(NativeComposeState.Command, ComposeMachine.Next(NativeComposeState.Shell, ComposeMachine.Trigger.CtrlVerb, bufferEmpty: true));
        Assert.Equal(NativeComposeState.Command, ComposeMachine.Next(NativeComposeState.Quick, ComposeMachine.Trigger.CtrlVerb, bufferEmpty: true));
        // With text the key is an ordinary printable instead.
        Assert.Equal(NativeComposeState.Chat, ComposeMachine.Next(NativeComposeState.Chat, ComposeMachine.Trigger.CtrlVerb, bufferEmpty: false));
        // MouseCommand does not take keystrokes at all.
        Assert.Equal(NativeComposeState.MouseCommand, ComposeMachine.Next(NativeComposeState.MouseCommand, ComposeMachine.Trigger.CtrlVerb, bufferEmpty: true));
    }

    [Fact]
    public void TakesKeystrokes_mirrors_the_native_state_order()
    {
        // console.cpp:2335-2338 — IsTakingKeystrokes: state > MouseCommand.
        Assert.False(ComposeMachine.TakesKeystrokes(NativeComposeState.NotComposing));
        Assert.False(ComposeMachine.TakesKeystrokes(NativeComposeState.MouseCommand));
        Assert.True(ComposeMachine.TakesKeystrokes(NativeComposeState.Chat));
        Assert.True(ComposeMachine.TakesKeystrokes(NativeComposeState.Command));
        Assert.True(ComposeMachine.TakesKeystrokes(NativeComposeState.Shell));
        Assert.True(ComposeMachine.TakesKeystrokes(NativeComposeState.Quick));
    }

    // ── 2. The shell compose ─────────────────────────────────────────────────

    private static IReadOnlyDictionary<short, ShipEntity> Roster(params (short Id, string Name)[] ships)
    {
        var world = new ClientWorld();
        foreach (var (id, name) in ships)
            world.Apply(new ExportShip { ShipId = id, SideId = 1, HullId = 707, CharacterName = name });
        return world.Ships;
    }

    [Fact]
    public void Shell_parse_mutes_and_unmutes_by_character_name()
    {
        // clintlib.cpp:2277-2296 — !mute / !unmute <player>, resolved by name.
        var ships = Roster((7, "Bob"), (9, "alice"));
        var mute = ShellCommands.Parse("!mute alice", ships);
        Assert.True(mute.Handled);
        Assert.Equal((short)9, mute.ShipId);
        Assert.True(mute.Mute);
        Assert.Equal("alice", mute.Name);

        var unmute = ShellCommands.Parse("!unmute Bob", ships);   // case-insensitive like FindPlayer
        Assert.True(unmute.Handled);
        Assert.Equal((short)7, unmute.ShipId);
        Assert.False(unmute.Mute);
    }

    [Theory]
    [InlineData("!mute")]
    [InlineData("!mute nobody")]
    [InlineData("!party Bob")]     // native ParseShellCommand ships exactly two commands
    [InlineData("mute Bob")]       // no '!' — not a shell line
    [InlineData("")]               // an empty buffer quits instead (console.cpp:2360-2363)
    public void Shell_parse_drops_unrecognised_lines(string line)
    {
        // clintlib.cpp:2310 — the bCommand result is ignored and the text is
        // never sent as chat: an unknown shell line just vanishes.
        Assert.False(ShellCommands.Parse(line, Roster((7, "Bob"))).Handled);
    }

    // ── 3. Per-recipient order fan-out ───────────────────────────────────────

    private static ClientWorld FanoutWorld()
    {
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = -1, SideId = 1, HullId = 707, CharacterName = "me" });   // own-ship export → MySideId
        world.Apply(new ExportShip { ShipId = 10, SideId = 1, HullId = 707, CharacterName = "wing-mate" });
        world.Apply(new ExportShip { ShipId = 11, SideId = 1, HullId = 707, CharacterName = "other-wing" });
        world.Apply(new ExportShip { ShipId = 12, SideId = 2, HullId = 707, CharacterName = "enemy" });
        world.Apply(new ExportShip { ShipId = 13, SideId = 1, HullId = 707, CharacterName = "drone" });
        // Ships 10 (me, wing 0 default) and 10/13 in wing 0; ship 11 in wing 1.
        world.Apply(new SetWingId { ShipID = 11, WingId = 1 });
        world.Apply(new SetWingId { ShipID = 13, WingId = 0 });
        return world;
    }

    [Fact]
    public void Fanout_group_selection_wins_over_wing_and_single()
    {
        // console.cpp:2128-2140 — the drag rectangle's Group subjects.
        var world = FanoutWorld();
        var group = new List<Game1.AutopilotTarget> { new(Game1.TargetKind.Ship, 10, "wing-mate"), new(Game1.TargetKind.Ship, 13, "drone") };
        var recipients = Game1.CommandFanout.Recipients(group, new Game1.AutopilotTarget(Game1.TargetKind.Ship, 10, "wing-mate"),
            wingSelected: true, wingId: 0, self: 99, world.Ships, world.MySideId);
        Assert.Equal(new short[] { 10, 13 }, recipients.Select(r => r.Id));
    }

    [Fact]
    public void Fanout_wing_selection_sends_to_every_same_side_wing_member_including_self()
    {
        // console.cpp:398-406 — the CHAT_WING subject sweep: EVERY side ship
        // with the matching wing, the player's own among them.
        var world = FanoutWorld();
        var recipients = Game1.CommandFanout.Recipients([], commandee: null,
            wingSelected: true, wingId: 0, self: 99, world.Ships, world.MySideId);
        // Ships 10, 13: same side, wing 0. 11 is wing 1, 12 is the enemy.
        Assert.Equal(new short[] { 10, 13 }, recipients.Select(r => r.Id).OrderBy(id => id));

        var otherWing = Game1.CommandFanout.Recipients([], null, wingSelected: true, wingId: 1, self: 99, world.Ships, world.MySideId);
        Assert.Equal(new short[] { 11 }, otherWing.Select(r => r.Id));
    }

    [Fact]
    public void Fanout_without_group_or_wing_is_the_single_commandee()
    {
        var world = FanoutWorld();
        var recipients = Game1.CommandFanout.Recipients([], new Game1.AutopilotTarget(Game1.TargetKind.Ship, 10, "wing-mate"),
            wingSelected: false, wingId: 0, self: 99, world.Ships, world.MySideId);
        Assert.Equal(new short[] { 10 }, recipients.Select(r => r.Id));

        Assert.Empty(Game1.CommandFanout.Recipients([], null, wingSelected: false, wingId: 0, self: 99, world.Ships, world.MySideId));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Fanout_issues_one_order_message_per_recipient_on_the_wire()
    {
        Require.Artwork();
        // The loopback half: a real server, three peers (2 sides). The wing-0
        // fan-out over the sender's real world is { self, teammate }; the
        // production loop (one SendChat per recipient, console.cpp:1546-1564)
        // then puts ONE FM_CS_CHATMESSAGE(23) order per recipient on the wire —
        // each recipient's log carries exactly one order addressed to IT.
        using var server = new Srv();
        server.Boot(new Cfg
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork),
            StaticCore = "PCore001.igc",
            Port = 0,
            UpdatesPerSecond = 20,
            SideSelectAutoSeatSeconds = 30,
            Games = [new Gm { Name = "P784 fanout", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], MaxTeamImbalance = 0x7fff }],
        });

        var sessions = new[] { new ConnectionSession(), new ConnectionSession(), new ConnectionSession() };
        string[] names = { "p784_sender", "p784_mate", "p784_enemy" };
        try
        {
            for (int i = 0; i < sessions.Length; i++)
            {
                sessions[i].Connect(server.BoundPort, names[i]);
                sessions[i].SendLogonRequest(new LogonRequest
                {
                    CharacterName = names[i], ZoneTicket = [0x01, 0x02, 0x03, 0x04],
                    CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
                    FedsrvVer = LogonRequest.MsgVer, Time = 446u, DwCookie = uint.MaxValue,
                    CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
                    SteamID = 90200000000000000ul + (ulong)i, DrmHash = "p784fanout",
                });
                // The contract needs two known teammates and an enemy, not
                // timing-dependent auto seating. Finish each explicit side
                // request before opening the next peer.
                Assert.True(PumpUntil(server, () => sessions[i].Ack is not null, 8000) < 8000);
                sessions[i].SendPositionReq(i == 2 ? (short)1 : (short)0);
                Assert.True(PumpUntil(server, () => sessions[i].World.MyShipId is not null, 8000) < 8000);
            }
            PumpUntil(server, () => sessions.All(s => s.World.MyShipId is not null), 8000);
            Assert.True(sessions.All(s => s.World.MyShipId is not null), "all three sessions must be assigned a ship");

            var sender = sessions[0];
            short senderSide = sender.World.Ships[sender.World.MyShipId!.Value].SideId;
            int mateIdx = -1, enemyIdx = -1;
            for (int i = 1; i < sessions.Length; i++)
            {
                short side = sessions[i].World.Ships[sessions[i].World.MyShipId!.Value].SideId;
                if (side == senderSide) mateIdx = i; else enemyIdx = i;
            }
            Assert.True(mateIdx >= 0 && enemyIdx >= 0, "3 peers / 2 teams must leave one teammate and one enemy");
            var mate = sessions[mateIdx];
            var enemy = sessions[enemyIdx];

            // Own assignment precedes the remaining join/peer notifications.
            // Drain the teammate's identity/wing before taking the fanout snapshot.
            Assert.True(PumpUntil(server, () =>
            {
                lock (sender.Gate)
                    return sender.World.Ships.TryGetValue(mate.World.MyShipId!.Value, out var teammate)
                        && teammate.SideId == senderSide && teammate.WingId == 0;
            }, 8000) < 8000);

            // Resolve the wing-0 recipients over the sender's real world. Native
            // includes EVERY same-side wing-0 ship in the CHAT_WING subject sweep
            // (console.cpp:398-406) — the side's nameless system ship among them
            // — so assert the two real players are in and every recipient is a
            // same-side wing-0 ship.
            var recipients = Game1.CommandFanout.Recipients([], commandee: null,
                wingSelected: true, wingId: 0, self: sender.World.MyShipId!.Value,
                sender.World.Ships, sender.World.MySideId);
            Assert.Contains(sender.World.MyShipId!.Value, recipients.Select(r => r.Id));
            Assert.Contains(mate.World.MyShipId!.Value, recipients.Select(r => r.Id));
            Assert.DoesNotContain(enemy.World.MyShipId!.Value, recipients.Select(r => r.Id));
            foreach (var r in recipients)
            {
                var ent = sender.World.Ships[r.Id];
                Assert.Equal(senderSide, ent.SideId);
                Assert.Equal((short)0, ent.WingId);
            }

            // The production per-recipient loop: one Individual order chat each
            // (Game1.TryIssueOrderToSingleCommandee's send, one per recipient).
            short targetShip = mate.World.MyShipId!.Value;
            foreach (var recipient in recipients)
                sender.SendChat("", ChatTarget.Individual, oidRecipient: recipient.Id,
                    commandId: OrderChange.CidAttack,
                    otTarget: (short)IgcObjectType.Ship, oidTarget: targetShip);

            PumpUntil(server, () => mate.World.Events.Chats.Any(m =>
                m.CommandID == OrderChange.CidAttack && m.OidRecipient == mate.World.MyShipId!.Value), 8000);
            // Each real recipient receives exactly ONE order addressed to IT —
            // the per-recipient wire fan-out, not one message to a single
            // target. (A system ship's copy has no connection and is dropped.)
            var mateOrders = mate.World.Events.Chats.Where(m =>
                m.CommandID == OrderChange.CidAttack && m.OidRecipient == mate.World.MyShipId!.Value).ToList();
            Assert.Single(mateOrders);
            Assert.Equal(sender.World.MyShipId!.Value, mateOrders[0].SidSender);

            PumpUntil(server, () => sender.World.Events.Chats.Any(m =>
                m.CommandID == OrderChange.CidAttack && m.OidRecipient == sender.World.MyShipId!.Value), 8000);
            var selfOrders = sender.World.Events.Chats.Where(m =>
                m.CommandID == OrderChange.CidAttack && m.OidRecipient == sender.World.MyShipId!.Value).ToList();
            Assert.Single(selfOrders);

            // The enemy side never sees any of the fan-out's order chats.
            Assert.DoesNotContain(enemy.World.Events.Chats,
                m => m.CommandID == OrderChange.CidAttack);
        }
        finally
        {
            foreach (var s in sessions) s.Dispose();
        }
    }

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // ── 4. Leader-bold chat history ──────────────────────────────────────────

    [Fact]
    public void Leader_lines_use_the_native_bold_history_face()
    {
        // chatpane.cpp:34,93 — TrekResources::GetFont(size, IsFromLeader()).
        Assert.Equal(NativeFont.LargeFont, ChatRenderer.HistoryFontKind(fromLeader: false));
        Assert.Equal(NativeFont.LargeBoldFont, ChatRenderer.HistoryFontKind(fromLeader: true));
    }

    [Fact]
    public void Chat_history_marks_the_side_leaders_lines_bold()
    {
        // trekigc.cpp:4498 — bIsLeader = ppi->IsTeamLeader(): the flag rides the
        // SENDER, resolved here from the port's per-side leader roster.
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = -1, SideId = 1, HullId = 707, CharacterName = "me" });
        world.Apply(new ExportShip { ShipId = 7, SideId = 1, HullId = 707, CharacterName = "leader" });
        world.Apply(new ExportShip { ShipId = 8, SideId = 1, HullId = 707, CharacterName = "wing" });
        world.Apply(new SetTeamLeader { SideId = 1, ShipId = 7 });
        world.Apply(new ChatMessage { Message = "from the leader", SidSender = 7, ChatTarget = (int)ChatTarget.Team });
        world.Apply(new ChatMessage { Message = "from a wingmate", SidSender = 8, ChatTarget = (int)ChatTarget.Team });

        var game = Headless(new ConnectionSession());
        var sessionField = typeof(Game1).GetField("_session", Hidden)!;
        var session = (ConnectionSession)sessionField.GetValue(game)!;
        // Swap the headless session's world for the seeded one (same trick the
        // loopback tests use — the production handler reads _session.World).
        typeof(ConnectionSession).GetField("_world", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, world);

        var result = Call<(List<string>, List<HudColor?>, List<bool>)>(game, "BuildChatLinesAndColors");
        Assert.Equal(2, result.Item1.Count);
        Assert.True(result.Item3[0], "the leader's line must be flagged");
        Assert.False(result.Item3[1], "a wingmate's line must not be flagged");
    }

    // ── 5. The production compose flow (headless Game1, the P761 idiom) ──────

    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(Game1 game, string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private static T Get<T>(Game1 game, string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private static object Call(Game1 game, string name, params object[] args) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args)!;
    private static T Call<T>(Game1 game, string name, params object[] args) => (T)typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args)!;

    private static Game1 Headless(ConnectionSession session)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_session", session);
        Set(game, "_moderation", new ModerationModel());
        Set(game, "_chatPane", new ChatPaneModel());
        Set(game, "_chatBuffer", new System.Text.StringBuilder());
        Set(game, "_chatRecipient", new ChatSubjects());   // P818: native m_chsChat (ChatSubject)
        Set(game, "_pilotProfile", new PilotInfoLayout.ProfileEditor());
        var state = typeof(Game1).GetField("_state", Hidden)!;
        state.SetValue(game, Enum.Parse(state.FieldType, "Flying"));
        return game;
    }

    private static void Type(Game1 game, string text)
    {
        foreach (char c in text)
            Call(game, "OnTextInput", null!, new Microsoft.Xna.Framework.TextInputEventArgs(c, XnaKeys.None));
    }

    private static Microsoft.Xna.Framework.Input.KeyboardState Ks(params XnaKeys[] keys) => new(keys);

    [Fact]
    public void Typing_bang_on_a_fresh_compose_switches_to_the_shell_state()
    {
        // console.cpp:2388-2396 — '!' while composing CHAT with an empty buffer.
        var game = Headless(new ConnectionSession());
        Call(game, "HandleChatCompose", Ks(XnaKeys.Enter));   // open the composer
        Type(game, "!");
        Assert.Equal(NativeComposeState.Shell, Get<NativeComposeState>(game, "_composeState"));
        Assert.Equal("!", Get<System.Text.StringBuilder>(game, "_chatBuffer").ToString());
        Assert.True(Get<bool>(game, "_chatActive"));           // the shell line keeps taking keystrokes
    }

    [Fact]
    public void Typing_bang_after_text_stays_a_printable_character()
    {
        var game = Headless(new ConnectionSession());
        Call(game, "HandleChatCompose", Ks(XnaKeys.Enter));
        Type(game, "hi!");
        Assert.Equal(NativeComposeState.Chat, Get<NativeComposeState>(game, "_composeState"));
        Assert.Equal("hi!", Get<System.Text.StringBuilder>(game, "_chatBuffer").ToString());
    }

    [Fact]
    public void Enter_in_the_shell_state_runs_the_shell_command_not_chat()
    {
        // console.cpp:1612-1614 — a Shell line goes to ParseShellCommand, never
        // to the chat relay; QuitComposing closes the composer either way.
        var session = new ConnectionSession();
        session.World.Apply(new ExportShip { ShipId = 7, SideId = 1, HullId = 707, CharacterName = "Bob" });
        var game = Headless(session);

        Call(game, "HandleChatCompose", Ks(XnaKeys.Enter));   // open
        Call(game, "HandleChatCompose", Ks());                // release
        Type(game, "!mute Bob");
        Call(game, "HandleChatCompose", Ks(XnaKeys.Enter));   // send
        Call(game, "HandleChatCompose", Ks());                // release

        Assert.Equal(NativeComposeState.NotComposing, Get<NativeComposeState>(game, "_composeState"));
        Assert.False(Get<bool>(game, "_chatActive"));
        Assert.True(session.World.IsPilotMuted(7), "the shell line must mute Bob, not send chat");

        Type(game, "!unmute Bob");   // nothing is composing — plain text must be dropped
        Assert.Equal(string.Empty, Get<System.Text.StringBuilder>(game, "_chatBuffer").ToString());
        Assert.True(session.World.IsPilotMuted(7), "a shell verb needs a live compose — the stray text changes nothing");
    }

    [Fact]
    public void Escape_in_the_command_state_steps_back_to_chat()
    {
        // console.cpp:2375-2380 — VK_ESCAPE while composing a COMMAND clears the
        // text and moves the compose BACK to Chat. The Escape chain routes it:
        // the port's composing gate for Escape includes the Command compose.
        var game = Headless(new ConnectionSession());
        Call(game, "SetComposeState", NativeComposeState.Command);
        Type(game, "att");   // native clears the typed text on this transition

        var live = (NativeComposeState)typeof(Game1).GetProperty("LiveComposeState", Hidden)!.GetValue(game)!;
        Assert.Equal(EscapeUiAction.Chat, MainMenuRules.Escape(
            briefing: false, chat: live is NativeComposeState.Command or NativeComposeState.MouseCommand || Get<bool>(game, "_chatActive"),
            quickChat: false, controls: false, help: false, credits: false, mainMenuPressed: false));

        Call(game, "SetComposeState", NativeComposeState.Chat);   // the Chat action's step-back
        Assert.Equal(NativeComposeState.Chat, Get<NativeComposeState>(game, "_composeState"));
        Assert.True(Get<bool>(game, "_chatActive"));
    }

    [Fact]
    public void A_recipient_bar_click_maps_the_native_columns_to_chat_targets()
    {
        // console.cpp:13-18,1140-1175 — Me/Sector/Team/All/WingN.
        Assert.Equal(ChatTarget.Individual, CommandBarLayout.RecipientChatTarget(0));
        Assert.Equal(ChatTarget.FriendlySector, CommandBarLayout.RecipientChatTarget(1));
        Assert.Equal(ChatTarget.Team, CommandBarLayout.RecipientChatTarget(2));
        Assert.Equal(ChatTarget.Everyone, CommandBarLayout.RecipientChatTarget(3));
        Assert.Equal(ChatTarget.Wing, CommandBarLayout.RecipientChatTarget(4 + 5));
        Assert.Equal(ChatTarget.NoSelection, CommandBarLayout.RecipientChatTarget(99));
    }

    [Fact]
    public void A_ship_pick_after_a_wing_pick_replaces_the_wing_recipient()
    {
        // console.cpp:1809,2166,2176 — picking one ship sets CHAT_INDIVIDUAL, so the
        // earlier wing recipient no longer fans the next order out to the wing.
        var game = Headless(new ConnectionSession());
        Set(game, "_commandRecipientColumn", 4 + 2);
        Call(game, "ClearWingRecipient");
        Assert.True(Get<int>(game, "_commandRecipientColumn") < 4);

        Set(game, "_commandRecipientColumn", 1);   // a non-wing recipient is left alone
        Call(game, "ClearWingRecipient");
        Assert.Equal(1, Get<int>(game, "_commandRecipientColumn"));
    }
}
