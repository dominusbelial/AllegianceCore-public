using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Xunit;
using Orientation = Allegiance.Core.Sim.Orientation;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P787b — pins the two remaining UI-hover/click sound rows and the loop-cue
/// rules:
/// <list type="bullet">
/// <item><b>SIM-0321/0327</b> — the docked loadout list's mouseover cue
/// (native <c>OnListHover</c>, loadout.cpp:1173-1190; the slot MouseEnter,
/// load.cpp:233-237), through the <see cref="IUiSoundSink"/> seam
/// (<c>Game1.RunShopHover</c>, the P764b split pattern) and the pure
/// <see cref="ShopHover.Hover"/> rule.</item>
/// <item><b>SIM-0332</b> — the plain RIGHT-click cluster PickCluster
/// (sectormap.cpp:1583-1587 + console.cpp:1739-1791): the mouseclickSound call
/// and the compose-recipient state (<c>Game1.RunConsolePickCluster</c>), plus
/// the recipient override reaching the real chat send (the FriendlySector
/// recipient is the right-clicked cluster).</item>
/// <item><b>SIM-0347/0348</b> — the weapon activate-loop and turret turn-sound
/// id resolution (<see cref="WeaponSoundMap"/> tables) and the native
/// <c>TurnRate &gt; 0.01</c> predicate (<see cref="TurretTurnCue"/>).</item>
/// <item><b>SIM-0345/0411</b> — the ambient switch decision
/// (<see cref="AmbientSwitchRule"/>): start / none / switch / stop, native
/// trekigc.cpp:5076-5087.</item>
/// </list>
/// </summary>
public sealed class P787BShopHoverAndPickTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static;

    private sealed class RecordingUiSink : IUiSoundSink
    {
        public readonly List<string> Calls = new();
        public void PlayMouseOver() => Calls.Add("mouseover");
        public void PlayMapClick() => Calls.Add("mouseclick");
        public void PlayError() => Calls.Add("error");
        public void PlayPositiveClick() => Calls.Add("positive");
        public void PlayMountPart() => Calls.Add("mount");
        public void PlayVoice(string assetStem) => Calls.Add($"voice:{assetStem}");
        public void PlayPersonalJump() => Calls.Add("personaljump");
    }

    private static void Set(Game1 game, string name, object value) =>
        typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);

    private static T Get<T>(Game1 game, string name) =>
        (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;

    private static object? Call(Game1 game, string name, params object?[] args) =>
        typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);

    private static Game1 Headless(ConnectionSession session, RecordingUiSink sink)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1)); GC.SuppressFinalize(game);
        Set(game, "_session", session);
        Set(game, "_uiSoundOverride", sink);
        Set(game, "_chatBuffer", new System.Text.StringBuilder());
        Set(game, "_moderation", new ModerationModel());
        Set(game, "_chatPane", new ChatPaneModel());
        Set(game, "_pilotProfile", new PilotInfoLayout.ProfileEditor());
        Set(game, "_chatRecipient", new ChatSubjects());   // P818: native m_chsChat (ChatSubject)
        var state = typeof(Game1).GetField("_state", Hidden)!;
        state.SetValue(game, Enum.Parse(state.FieldType, "Flying"));
        return game;
    }

    // ── SIM-0321/0327 — the docked list hover ─────────────────────────────

    [Theory]
    [InlineData(0, -1, true)]    // fresh entry onto row 0 — native enters → cue
    [InlineData(2, 2, false)]    // same row again (no leave between) — no cue
    [InlineData(1, 2, true)]     // moved to a different row — cue
    [InlineData(-1, 2, false)]   // left the list — no cue, index resets
    [InlineData(null, -1, false)]
    public void ShopHover_rule_matches_native_OnListHover(int? hovered, int lastHover, bool expectedCue)
    {
        var (last, cue) = ShopHover.Hover(hovered, lastHover);
        Assert.Equal(expectedCue, cue);
        Assert.Equal(hovered is { } r && r >= 0 ? r : -1, last);
    }

    [Fact]
    public void Leaving_then_reentering_a_row_cues_again()
    {
        var (mid, cue1) = ShopHover.Hover(3, -1);
        var (off, cue2) = ShopHover.Hover(null, mid);
        var (back, cue3) = ShopHover.Hover(3, off);
        Assert.True(cue1); Assert.False(cue2); Assert.True(cue3);
        Assert.Equal(3, back);
    }

    [Fact]
    public void RunShopHover_cues_mouseover_once_per_change_and_moves_the_cursor()
    {
        using var session = new ConnectionSession();
        var sink = new RecordingUiSink();
        var game = Headless(session, sink);
        Set(game, "_shopModel", new ShopModel(ShopCatalog.Empty, currentHullId: 1,
            Array.Empty<ExpandedPartData>(), money: 0));
        Call(game, "RunShopHover", 1);
        Call(game, "RunShopHover", 1);   // same row — no second cue
        Call(game, "RunShopHover", 2);
        Call(game, "RunShopHover", new object?[]{ null });
        Call(game, "RunShopHover", 2);   // re-entry after leaving cues again (native resets on leave)
        Assert.Equal(new[] { "mouseover", "mouseover", "mouseover" }, sink.Calls);
        Assert.Equal(2, Get<int>(game, "_shopHoverRow"));   // last hover tracks the re-entered row
    }

    [SkippableFact]
    public void A_resting_pointer_does_not_undo_keyboard_navigation()
    {
        Allegiance.TestInfra.Require.Artwork();
        // Native OnListHover acts on a hover CHANGE (loadout.cpp:1168-1184); the port's
        // cursor follows the hovered row only when that row changes.
        var catalog = ShopCatalog.TryLoad(ExternalPaths.Resolve(ExternalResourceKind.Artwork))
            ?? throw new InvalidOperationException("PCore001.igc not loadable");
        using var session = new ConnectionSession();
        var game = Headless(session, new RecordingUiSink());
        var hull = catalog.Hulls.First(h => h.GroupId >= 0 && h.Hardpoints.Length >= 3);
        var shop = new ShopModel(catalog, currentHullId: hull.HullId, Array.Empty<ExpandedPartData>(), money: 0);
        Assert.True(shop.RowCount >= 3, $"fixture needs 3 rows, has {shop.RowCount}");
        Set(game, "_shopModel", shop);

        Call(game, "RunShopHover", 1);
        Assert.Equal(1, shop.Cursor);
        shop.MoveCursor(1);                       // keyboard down
        Call(game, "RunShopHover", 1);            // the pointer has not moved
        Assert.Equal(2, shop.Cursor);
        Call(game, "RunShopHover", 0);            // a real hover change still follows
        Assert.Equal(0, shop.Cursor);
    }

    // ── SIM-0332 — the plain right-click PickCluster ─────────────────────

    [Fact]
    public void Plain_right_click_cues_mouseclick_and_composes_chat_to_that_sector()
    {
        var s = new ConnectionSession();
        var w = s.World;
        w.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(10, 0, default, 0, Vector3.Zero,
            Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        w.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "p787b" });
        w.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        var sink = new RecordingUiSink();
        var game = Headless(s, sink);

        Call(game, "RunConsolePickCluster", (short)7);

        Assert.Equal(new[] { "mouseclick" }, sink.Calls);   // sectormap.cpp:1586
        Assert.Equal(ChatTarget.FriendlySector, Get<ChatSubjects>(game, "_chatRecipient").Target);
        Assert.Equal((short)7, Get<short?>(game, "_chatSectorRecipient"));
        Assert.True(Get<bool>(game, "_chatActive"));        // console.cpp:1744 StartComposing(c_csComposeChat)
    }

    [Fact]
    public void Right_click_on_empty_map_cues_nothing()
    {
        var s = new ConnectionSession();
        var sink = new RecordingUiSink();
        var game = Headless(s, sink);
        Call(game, "RunConsolePickCluster", (short)-1);
        Assert.Empty(sink.Calls);
        Assert.Equal(ChatTargetLogic.DefaultTarget, Get<ChatSubjects>(game, "_chatRecipient").Target);
    }

    [Fact]
    public void Right_click_while_composing_switches_to_command_and_keeps_typing_state()
    {
        var s = new ConnectionSession();
        var w = s.World;
        w.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(10, 0, default, 0, Vector3.Zero,
            Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        w.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "p787b" });
        w.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        var sink = new RecordingUiSink();
        var game = Headless(s, sink);
        Set(game, "_composeState", NativeComposeState.Chat);
        Set(game, "_chatActive", true);

        Call(game, "RunConsolePickCluster", (short)3);

        // The click still cues (sectormap.cpp:1586 fires before PickCluster);
        // the compose becomes the command compose (console.cpp:1754-1756) and
        // the goto order send is the c_cidGoto buoy leg (console.cpp:1782-1791).
        Assert.Equal(new[] { "mouseclick" }, sink.Calls);
        Assert.Equal(NativeComposeState.Command, Get<NativeComposeState>(game, "_composeState"));
    }

    /// <summary>The full loopback: right-click picks sector 7 as the compose's
    /// FriendlySector recipient, and the composed line reaches the wire
    /// addressed to THAT cluster (the recipient override in the send path).</summary>
    [Fact]
    public async Task Picked_sector_is_the_chat_recipient_on_the_wire()
    {
        var site = new Site();
        var host = new TcpTransport(site);
        host.Host(0);
        using var session = new ConnectionSession();
        session.Connect(host.BoundPort, "P787B pick");
        await site.Joined.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var game = Headless(session, new RecordingUiSink());
        var w = session.World;
        w.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(10, 0, default, 0, Vector3.Zero,
            Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        w.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "p787b" });
        w.Apply(new ExportShip { ShipId = -1, SideId = 0 });

        Call(game, "RunConsolePickCluster", (short)7);
        Set(game, "_chatBuffer", new System.Text.StringBuilder("cover me"));
        Call(game, "SendChatLine");

        ChatMessage? sent = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sent is null && DateTime.UtcNow < deadline)
        {
            while (site.Received.TryDequeue(out var m))
                if (FedMessageHeader.Read(m).Fmid == ChatMessage.Fmid)
                    sent = ChatMessage.Decode(m);
            if (sent is null) await Task.Delay(5);
        }
        Assert.NotNull(sent);
        Assert.Equal((int)ChatTarget.FriendlySector, sent!.ChatTarget);
        Assert.Equal(7, sent.OidRecipient);   // the right-clicked cluster, not the own sector
        Assert.Equal("cover me", sent.Message);
    }

    private sealed class Site : ITransportSite
    {
        public readonly TaskCompletionSource<ConnectionId> Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<byte[]> Received = new();
        public void OnPeerJoined(ConnectionId id, string name) => Joined.TrySetResult(id);
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId id, ReadOnlySpan<byte> bytes)
        { foreach (var message in FedPacket.Unpack(bytes)) Received.Enqueue(message); }
    }

    // ── SIM-0345/0411 — the ambient switch ────────────────────────────────

    [Theory]
    [InlineData(null, 0, AmbientSwitchRule.Action.None)]     // nothing playing, nothing requested
    [InlineData(null, 425, AmbientSwitchRule.Action.Start)]  // docked: start the interior loop
    [InlineData((short)425, (short)425, AmbientSwitchRule.Action.None)]  // same station: keep
    [InlineData((short)425, (short)440, AmbientSwitchRule.Action.Switch)] // moved stations: restart
    [InlineData((short)425, (short)0, AmbientSwitchRule.Action.Stop)]    // undocked: kill (trekigc.cpp:5093-5098)
    [InlineData(null, (short)0, AmbientSwitchRule.Action.None)]
    public void Ambient_switch_follows_native_trekigc_5076_5087(short? playing, short requested, AmbientSwitchRule.Action expected)
        => Assert.Equal(expected, AmbientSwitchRule.Next(playing, requested));

    // ── SIM-0345 — the asteroid ambient curve ──────────────────────────

    [Fact]
    public void Asteroid_ambient_curve_is_native_authored_200_unit_falloff()
    {
        Assert.Equal(200f, Audio.AsteroidAmbientSoundCurve.AudibleRadius);  // ThreeDSound(200)
        Assert.Equal(Audio.AsteroidAmbientSoundCurve.MaxVolume,
            Audio.AsteroidAmbientSoundCurve.VolumeForDistance(0f));
        Assert.Equal(0f, Audio.AsteroidAmbientSoundCurve.VolumeForDistance(200f));
        float mid = Audio.AsteroidAmbientSoundCurve.VolumeForDistance(100f);
        Assert.Equal(Audio.AsteroidAmbientSoundCurve.MaxVolume * 0.5f, mid, 4);
        Assert.Equal(0f, Audio.AsteroidAmbientSoundCurve.VolumeForDistance(500f));  // clamped past the radius
    }

    // ── SIM-0347/0348 — activate + turn sound id resolution ──────────────

    [Fact]
    public void Activation_id_table_resolves_every_real_PCore001_weapon_activate_id()
    {
        // The corpus dump (P787B report §assets): 44 distinct activation ids.
        short[] ids = [81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97,
            99, 100, 101, 102, 103, 104, 105, 106, 107, 109, 110, 111, 113, 114, 115, 116,
            127, 128, 129, 130, 131, 132, 142, 530, 533, 550, 553];
        foreach (var id in ids)
        {
            Assert.True(WeaponSoundMap.TryResolveActivation(id, out var stem), $"id {id}");
            Assert.EndsWith("pwrup", stem);
        }
        Assert.False(WeaponSoundMap.TryResolveActivation(0, out _));   // NA — native StartSound(NA): silence
        Assert.False(WeaponSoundMap.TryResolveActivation(999, out _)); // unlisted: silence, no fallback
    }

    [Fact]
    public void Turn_sound_table_resolves_the_three_turret_tiers()
    {
        Assert.True(WeaponSoundMap.TryResolveTurn(120, out var small));
        Assert.Equal("sturret", small);
        Assert.True(WeaponSoundMap.TryResolveTurn(121, out var medium));
        Assert.Equal("mturret", medium);
        Assert.True(WeaponSoundMap.TryResolveTurn(122, out var large));
        Assert.Equal("lturret", large);
        Assert.False(WeaponSoundMap.TryResolveTurn(0, out _));
    }

    [Fact]
    public void Turret_turn_predicate_is_native_turn_rate_squared_above_0_01()
    {
        // native TurnRate() = yaw²+pitch²+roll² (trekigc.cpp:807-813), gated > 0.01 (:996)
        Assert.False(TurretTurnCue.ShouldLoop(0.05f, 0.05f, 0.05f));   // 0.0075 ≤ 0.01
        Assert.True(TurretTurnCue.ShouldLoop(0.1f, 0.1f, 0.1f));       // 0.03 > 0.01
        Assert.True(TurretTurnCue.ShouldLoop(0.2f, 0f, 0f));
        Assert.False(TurretTurnCue.ShouldLoop(0f, 0f, 0f));
    }

    [Fact]
    public void First_turret_turn_sound_is_the_first_non_fixed_hardpoint_with_one()
    {
        static DataHullTypeIGC.Hardpoint Hp(short turn, bool fixed_) =>
            new(InteriorSound: 0, TurnSound: turn, FrameName: "", LocationAbreviation: "",
                PartMask: 0, Fixed: fixed_);
        var hull = new DataHullTypeIGC(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "t", Description: "", GroupId: 0,
            TtbmRequired: Array.Empty<byte>(), TtbmEffects: Array.Empty<byte>(), Mass: 1f, Signature: 1f,
            Speed: 1f, MaxTurnRates: new[] { 1f, 1f, 1f }, TurnTorques: new[] { 1f, 1f, 1f },
            Thrust: 1f, SideMultiplier: 1f, BackMultiplier: 1f, ScannerRange: 1f, MaxFuel: 1f, Ecm: 0f,
            Length: 1f, MaxEnergy: 1f, RechargeRate: 1f, RipcordSpeed: 1f, RipcordCost: 0f, MaxAmmo: 0,
            HullId: 1, SuccessorHullId: -1, MaxWeapons: 3, MaxFixedWeapons: 1, HitPoints: 1f,
            HardpointOffset: 0, DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0,
            CapacityChaffLauncher: 0, PreferredPartsTypes: Array.Empty<short>(), HabmCapabilities: 0,
            TextureName: "", PmEquipment: new short[8], InteriorSound: 0, ExteriorSound: 0,
            MainThrusterInteriorSound: 0, MainThrusterExteriorSound: 0,
            ManuveringThrusterInteriorSound: 0, ManuveringThrusterExteriorSound: 0,
            Hardpoints: [Hp(0, false), Hp(121, true), Hp(122, false)]);
        // The first hardpoint is non-fixed but silent (0) — skipped; the fixed
        // 121 is a fixed gun (no gunner) — skipped; 122 is the first turret.
        Assert.Equal((short)122, TurretTurnCue.FirstTurretTurnSound(hull));
        Assert.Equal((short)0, TurretTurnCue.FirstTurretTurnSound(
            hull with { Hardpoints = [Hp(0, false)] }));
    }
}
