using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework.Input;
using Orientation = Allegiance.Core.Sim.Orientation;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P764 — pins the UI one-shot sound CALLS at their trigger sites (SIM-0328,
/// 0329, 0332, 0333, 0334/0335, 0336, 0285, 0325/0326, 0337) through the
/// <see cref="IUiSoundSink"/> seam: a headless <see cref="Game1"/>
/// (P748's <c>RuntimeHelpers.GetUninitializedObject</c> discipline) with a
/// recording sink installed via <c>Game1._uiSoundOverride</c>. Native plays
/// these cues with direct <c>trekClient.PlaySoundEffect(...)</c> calls at
/// each cited site; the port routes every one of them through the sink so a
/// test can observe the call, not just the bundled wave.
/// </summary>
public sealed class P764UiSoundSeamTests
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
        // P764b — AttemptTeleportTo resolves station types (the c_sabmRestart
        // check) through the shop catalogue; every teleport test below needs a
        // catalogue whose type 1 is RESTART-capable (and type 5 is not).
        Set(game, "_shopCatalog", TestCatalog);
        // GetUninitializedObject skips field initializers — the readonly
        // stateful helpers must be installed explicitly (P748's discipline).
        Set(game, "_teamPaneControls", new Flight.TeamPaneControls());
        Set(game, "_loadoutDigitWasDown", new bool[10]);
        var state = typeof(Game1).GetField("_state", Hidden)!;
        state.SetValue(game, Enum.Parse(state.FieldType, "Flying"));
        return game;
    }

    /// <summary>P764b — a synthetic station type with the given sabm bit set:
    /// the shape FindFriendlyStation's capability check consumes
    /// (sectormap.cpp:881, c_sabmRestart = 0x04).</summary>
    private static DataStationTypeIGC StationType(short id, short sabm) => new(
        Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: $"st{id}", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Signature: 0f,
        MaxArmorHitPoints: 0f, MaxShieldHitPoints: 0f, ArmorRegeneration: 0f, ShieldRegeneration: 0f,
        ScannerRange: 0f, Income: 0, Radius: 0f, TtbmLocal: [],
        StationTypeId: id, SuccessorStationTypeId: -1, DefenseTypeArmor: 0, DefenseTypeShield: 0,
        SabmCapabilities: sabm, AabmBuild: 0, ClassId: 0, ConstructionDroneTypeId: -1,
        ConstructorNeedRockSound: -1, ConstructorUnderAttackSound: -1, ConstructorDestroyedSound: -1,
        CompletionSound: -1, InteriorSound: -1, ExteriorSound: -1, InteriorAlertSound: -1,
        UnderAttackSound: -1, CriticalSound: -1, DestroyedSound: -1, CapturedSound: -1,
        EnemyCapturedSound: -1, EnemyDestroyedSound: -1, TextureName: "", BuilderName: "");

    /// <summary>P764b — the catalogue the teleport tests read: type 1 has
    /// c_sabmRestart, type 5 does not (DataStationTypeIGC.SabmRestart = 0x04).</summary>
    private static readonly Rendering.ShopCatalog TestCatalog = new([], [], StationTypes:
        new Dictionary<short, DataStationTypeIGC>
        {
            [1] = StationType(1, DataStationTypeIGC.SabmRestart),
            [5] = StationType(5, 0),
        });

    /// <summary>An own ship DOCKED at station 42 in cluster 1; stations 42
    /// (cluster 1, own side) and 43 (cluster 2, own side); cluster 3 empty.
    /// P764b — <paramref name="station43Type"/> selects station 43's type id
    /// (1 = RESTART-capable, 5 = not) for the FindFriendlyStation filter test.</summary>
    private static ConnectionSession DockedWorld(out ClientWorld world, short station43Type = 1)
    {
        var s = new ConnectionSession();
        var w = s.World;
        w.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(10, 0, default, 0, Vector3.Zero,
            Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        w.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "p764" });
        w.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        w.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, StationId = 42, ParentId = -1,
            State = ShipStatusMessage.StateDocked });
        w.Apply(new ExportStation { Station = Station(42, cluster: 1) });
        w.Apply(new ExportStation { Station = Station(43, cluster: 2, stationTypeId: station43Type) });
        world = w;
        return s;
    }

    private static DataStationIGC Station(short id, short cluster, short stationTypeId = 1) => new(
        Position: [0f, 0f, -500f], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], ClusterId: cluster, SideId: 0, StationId: id,
        StationTypeId: stationTypeId, BpHull: 1, BpShield: 1, Name: $"P764-{id}");

    // ── SIM-0328 / SIM-0329 — sector-map teleport refusals ─────────────────

    [Fact]
    public void TeleportIntoTheDockedCluster_PlaysError_AndPostsTheNativeLine()
    {
        // native sectormap.cpp:1362-1364: "You are already in this cluster."
        // + trekClient.PlaySoundEffect(errorSound).
        var sink = new RecordingUiSink();
        using var session = DockedWorld(out var world);
        var game = Headless(session, sink);
        Set(game, "_teleportActive", true);

        Call(game, "RunStarmapClick", (short)1, false);   // teleportOpen → AttemptTeleportTo(1)

        Assert.Contains("error", sink.Calls);
        Assert.Equal("You are already in this cluster.", LastLine(world));
    }

    [Fact]
    public void TeleportIntoAClusterWithoutAppropriateStation_PlaysError_AndPostsTheNativeLine()
    {
        // native sectormap.cpp:1365-1368: "You do not have an appropriate
        // station in this cluster." + errorSound. Cluster 3 has no station.
        var sink = new RecordingUiSink();
        using var session = DockedWorld(out var world);
        var game = Headless(session, sink);
        Set(game, "_teleportActive", true);

        Call(game, "RunStarmapClick", (short)3, false);

        Assert.Contains("error", sink.Calls);
        Assert.Equal("You do not have an appropriate station in this cluster.", LastLine(world));
    }

    [Fact]
    public void TeleportIntoAClusterWithAStation_SendsDocked_AndPlaysNoError()
    {
        var sink = new RecordingUiSink();
        using var session = DockedWorld(out _);
        var game = Headless(session, sink);
        Set(game, "_teleportActive", true);

        Call(game, "RunStarmapClick", (short)2, false);   // station 43 (type 1: c_sabmRestart) lives here

        Assert.DoesNotContain("error", sink.Calls);
        Assert.True(Get<bool>(game, "_launchSent"));
        Assert.False(Get<bool>(game, "_teleportActive"));
    }

    [Fact]
    public void TeleportIntoAClusterWhoseStationLacksRestart_IsNotAnAppropriateStation()
    {
        // P764b — native FindFriendlyStation only accepts stations whose type
        // HasCapability(c_sabmRestart) (sectormap.cpp:881); the port now
        // applies the same filter, so a cluster whose only station lacks the
        // bit falls into the "no appropriate station" error branch
        // (sectormap.cpp:1365-1368).
        var sink = new RecordingUiSink();
        using var session = DockedWorld(out var world, station43Type: 5);   // type 5: no c_sabmRestart
        var game = Headless(session, sink);
        Set(game, "_teleportActive", true);

        Call(game, "RunStarmapClick", (short)2, false);

        Assert.Contains("error", sink.Calls);
        Assert.Equal("You do not have an appropriate station in this cluster.", LastLine(world));
    }

    [Fact]
    public void TeleportAttemptWhileNotDocked_IsTheNativeRaceConditionSilence()
    {
        // native sectormap.cpp:1359-1361: no station → empty branch, no sound.
        var sink = new RecordingUiSink();
        using var session = DockedWorld(out var world);
        var game = Headless(session, sink);
        Set(game, "_teleportActive", true);
        // Undock the ship in place: station 42's cluster no longer matters.
        world.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, StationId = -1, ParentId = -1,
            State = ShipStatusMessage.StateFlying });

        Call(game, "RunStarmapClick", (short)2, false);

        // The attempt itself is silent (no error cue); the LEFT-click cluster
        // cue still played before the branches, exactly like native
        // (sectormap.cpp:1523 plays for every left-button cluster hit).
        Assert.DoesNotContain("error", sink.Calls);
    }

    // ── SIM-0332 — the right-button paths ─────────────────────────────────

    [Fact]
    public void RightClickClusterMark_PlaysNoCue_TheConsolePickClusterIsNotPorted()
    {
        // P764b — SIM-0332 correction: the port's `mark` path is the Ctrl+right
        // click HIGHLIGHT (native sectormap.cpp:1491-1510), which plays no
        // sound. Native's mouseclickSound on a right-button hit belongs to the
        // plain right-click console PickCluster (sectormap.cpp:1583-1587), a
        // path this port does not implement — the row is `missing` for it.
        var sink = new RecordingUiSink();
        using var session = DockedWorld(out _);
        var game = Headless(session, sink);

        Call(game, "RunStarmapClick", (short)2, true);

        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void LeftClickCluster_PlaysTheSameClickCue_BeforeTheBranches()
    {
        // native sectormap.cpp:1523 (left-button cluster hit): the cue plays
        // before any branch action. A no-op branch (ripcord off, teleport
        // closed, undocked, own sector) still cues.
        var sink = new RecordingUiSink();
        using var session = DockedWorld(out var world);
        var game = Headless(session, sink);
        world.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 2, StationId = -1, ParentId = -1,
            State = ShipStatusMessage.StateFlying });

        Call(game, "RunStarmapClick", (short)2, false);   // own sector → native "nothing"

        Assert.Equal(["mouseclick"], sink.Calls);
    }

    // ── SIM-0333 / SIM-0336 — team pane gift + selection ───────────────────

    private static ConnectionSession TeamPaneWorld(out ClientWorld world)
    {
        var s = new ConnectionSession();
        var w = s.World;
        w.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(10, 0, default, 0, Vector3.Zero,
            Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        foreach (short id in new short[] { 10, 11 })
            w.Apply(new ExportShip { ShipId = id, SideId = 0, CharacterName = $"p{id}" });
        w.Apply(new ScoreRosterUpdate { Entries =
            [new("p10", 0, 0, 0, 0, 0, 0), new("p11", 0, 0, 0, 0, 0, 0)] });
        w.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        w.Apply(new SetMoney { ShipId = 10, Money = 250 });
        w.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, StationId = -1, ParentId = -1,
            State = ShipStatusMessage.StateFlying });
        w.Apply(new ShipStatusMessage { ShipId = 11, SectorId = 1, StationId = -1, ParentId = -1,
            State = ShipStatusMessage.StateFlying });
        world = w;
        return s;
    }

    [Fact]
    public void TeamPaneGift_PlaysPositiveClick_AtTheNativeSite()
    {
        // native teampane.cpp:1173-1177: selected other player + money > 0 →
        // PlaySoundEffect(positiveButtonClickSound) before the send.
        var sink = new RecordingUiSink();
        using var session = TeamPaneWorld(out _);
        var game = Headless(session, sink);
        Set(game, "_teamPaneVisible", true);

        Call(game, "HandleTeamPaneControls", new KeyboardState(Keys.PageDown), false);  // select p10 (self)
        Call(game, "HandleTeamPaneControls", new KeyboardState(), false);              // release
        Call(game, "HandleTeamPaneControls", new KeyboardState(Keys.PageDown), false);  // select p11
        Call(game, "HandleTeamPaneControls", new KeyboardState(), false);              // release
        sink.Calls.Clear();   // SIM-0336's selection cue is pinned separately

        Call(game, "HandleTeamPaneControls", new KeyboardState(Keys.D), false);         // InvestDonate

        Assert.Equal(["positive"], sink.Calls);
    }

    [Fact]
    public void TeamPaneSelectionCycle_PlaysThePlayerListClickCue()
    {
        // native teamscreen.cpp:2434-2437 OnPlayerClicked: a player-list
        // selection change plays mouseclickSound. The pane's selection cycle is
        // the port's player-list selection.
        var sink = new RecordingUiSink();
        using var session = TeamPaneWorld(out _);
        var game = Headless(session, sink);
        Set(game, "_teamPaneVisible", true);

        Call(game, "HandleTeamPaneControls", new KeyboardState(Keys.PageDown), false);

        Assert.Equal(["mouseclick"], sink.Calls);
    }

    // ── SIM-0285 — help toggle ──────────────────────────────────────────────

    [Fact]
    public void HelpToggle_PlaysPositiveClick_OnOpenAndClose()
    {
        // native WinTrek.cpp:9267-9269 OnHelp: the cue on EVERY toggle.
        var sink = new RecordingUiSink();
        using var session = new ConnectionSession();
        var game = Headless(session, sink);

        Call(game, "ToggleHelpOverlay");
        Assert.True(Get<bool>(game, "_helpVisible"));
        Call(game, "ToggleHelpOverlay");
        Assert.False(Get<bool>(game, "_helpVisible"));

        Assert.Equal(["positive", "positive"], sink.Calls);
    }

    // ── SIM-0334 / SIM-0335 — team-screen row click ────────────────────────

    [Fact]
    public void SideSelectRowConfirm_PlaysTheTeamRowClickCue_AndSendsTheJoin()
    {
        // native teamscreen.cpp:2206-2222 OnTeamClicked/OnTeamDoubleClicked:
        // mouseclickSound on the team row; the double-click joins. The port's
        // side-select confirm is the team-row click (its join = POSITIONREQ).
        var sink = new RecordingUiSink();
        using var session = new ConnectionSession();
        var game = Headless(session, sink);

        Call(game, "RequestSideSelectSide", (short)3);

        Assert.Equal(["mouseclick"], sink.Calls);
    }

    // ── SIM-0337 — the noTurretsSound drain ────────────────────────────────

    [SkippableFact]
    public void BoardNackTurretRace_DrainsAsTheNoTurretsVoice()
    {
        // native teleportpane.cpp:282-286 OnBoardFailed: noTurretsSound. The
        // P757 recording seam (BoardRefusedTurretsFull) routes through
        // Game1's event drain to PlayVoice("vo_sal_noturrets") — pinned here
        // end-to-end with the recording sink.
        string root = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        if (root.Length == 0 || !File.Exists(Path.Combine(root, "PCore001.igc")))
            return;   // corpus absent on a clean clone (P597)
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(root, "PCore001.igc")));
        var turretHull = core.HullTypes.Values.First(h => h.MaxWeapons > h.MaxFixedWeapons);

        var sink = new RecordingUiSink();
        using var session = DockedWorld(out var world);
        world.MissionCoreResolver = _ => core;
        // JOINED_MISSION loads the mission core but clears the own-ship state
        // (the launch pose is not assigned yet), so the docked state is fed
        // AFTER it — the P638 wiring order.
        world.Apply(new MissionDef { SzIGCStaticFile = "pcore001" });
        world.Apply(new JoinedMission { ShipId = 10 });
        world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(10, 0, default, 0, Vector3.Zero,
            Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        world.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "p764" });
        world.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        world.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, StationId = 42, ParentId = -1,
            State = ShipStatusMessage.StateDocked });
        world.Apply(new EnterGame());
        world.Apply(new ExportShip { ShipId = 7, SideId = 0, HullId = turretHull.HullId, CharacterName = "mate" });
        world.Apply(new ShipStatusMessage { ShipId = 7, SectorId = 1, StationId = 42, ParentId = -1,
            HullId = turretHull.HullId, State = ShipStatusMessage.StateDocked });
        world.Apply(new BoardNack { SidRequestedParent = 7 });

        var game = Headless(session, sink);
        Call(game, "UpdateSoundEvents");

        Assert.Equal(["voice:vo_sal_noturrets"], sink.Calls);
    }

    // ── SIM-0325 / SIM-0326 — custom-loadout digit keys ────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void CtrlDigit_SavesTheLoadout_AndCuesPositiveClick_DigitRestoresWithMount()
    {
        // native load.cpp:2219-2250: Ctrl+0..9 saves ("Saved … loadout N" +
        // positiveButtonClickSound on success — the port's in-memory store
        // always succeeds); 0..9 restores ("Loaded … loadout N" + mountSound).
        string root = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        if (root.Length == 0 || !File.Exists(Path.Combine(root, "PCore001.igc")))
            return;
        var catalog = ShopCatalog.TryLoad(root) ?? throw new InvalidOperationException("catalogue missing");
        var hull = catalog.Hulls.OrderBy(h => h.HullId).First();
        var shop = new ShopModel(catalog, currentHullId: hull.HullId, currentParts: Array.Empty<ExpandedPartData>());

        var sink = new RecordingUiSink();
        using var session = new ConnectionSession();
        var game = Headless(session, sink);
        Set(game, "_shopModel", shop);

        // Native reads top-row digits only (ks.vk - 48, load.cpp:2223). Ctrl+3
        // saves the (empty) pending loadout into slot 3.
        Call(game, "HandleCustomLoadoutKeys", shop, new KeyboardState(Keys.D3, Keys.LeftControl));
        Assert.Equal(["positive"], sink.Calls);
        Call(game, "HandleCustomLoadoutKeys", shop, new KeyboardState());   // release the edge

        // Restoring the just-saved (empty) slot 3 stays empty — native
        // ReplaceLoadout with a FOUND (empty) cached link applies it
        // (clintlib.h:1797-1798); mountSound plays regardless (load.cpp:2247).
        Call(game, "HandleCustomLoadoutKeys", shop, new KeyboardState(Keys.D3));
        Assert.Equal(["positive", "mount"], sink.Calls);
        Assert.Empty(shop.ChosenParts);
        sink.Calls.Clear();

        // P764b — restoring an UNTOUCHED slot (5): native RefreshLoadout hands
        // the (absent) cached list to ReplaceLoadout, which buys the hull's
        // DEFAULT loadout when there is no cached loadout for the hull
        // (clintlib.h:1796-1800) — not an empty hold.
        var expectedDefault = new ShopModel(catalog, currentHullId: hull.HullId,
            currentParts: Array.Empty<ExpandedPartData>());
        expectedDefault.ApplyDefaultLoadout();
        Call(game, "HandleCustomLoadoutKeys", shop, new KeyboardState(Keys.D5));
        Assert.Equal(["mount"], sink.Calls);
        Assert.Equal(expectedDefault.ChosenParts, shop.ChosenParts);
    }

    // ── the pure teleport decision (both error branches + success) ─────────

    [Theory]
    [InlineData(-1, -1, 2, -1, Rendering.StarmapTeleport.OutcomeKind.NotDocked, false)]
    [InlineData(42, 1, 1, 43, Rendering.StarmapTeleport.OutcomeKind.AlreadyInCluster, true)]
    [InlineData(42, 1, 3, -1, Rendering.StarmapTeleport.OutcomeKind.NoStation, true)]
    [InlineData(42, 1, 2, 43, Rendering.StarmapTeleport.OutcomeKind.DockRequest, false)]
    public void StarmapTeleport_Decide_MatchesTheNativeBranchOrder(
        short dockedStation, short stationCluster, short clicked, short friendly,
        Rendering.StarmapTeleport.OutcomeKind kind, bool error)
    {
        var outcome = Rendering.StarmapTeleport.Decide(dockedStation, stationCluster, clicked, friendly);
        Assert.Equal(kind, outcome.Kind);
        Assert.Equal(error, outcome.PlaysError);
        if (kind == Rendering.StarmapTeleport.OutcomeKind.AlreadyInCluster)
            Assert.Equal("You are already in this cluster.", outcome.Message);
        if (kind == Rendering.StarmapTeleport.OutcomeKind.NoStation)
            Assert.Equal("You do not have an appropriate station in this cluster.", outcome.Message);
    }

    // ── SIM-0330 — the hover cue's call site (not only the pure rule) ──────

    [Fact]
    public void StarmapHoverCue_PlaysMouseOver_AtTheCallSite()
    {
        // P764b — SIM-0330's call pin: the Game1 trigger itself (the extracted
        // RunStarmapHover, the port of the sectormap.cpp:1440-1443
        // PlaySoundEffect(mouseoverSound) line), driven headless through the
        // sink — not only StarmapHover.Move's pure verdict.
        var sink = new RecordingUiSink();
        using var session = new ConnectionSession();
        var game = Headless(session, sink);

        Call(game, "RunStarmapHover", true, (short)2, (short?)null);   // new cluster under the pointer
        Assert.Equal(["mouseover"], sink.Calls);
        Assert.Equal((short)2, Get<short?>(game, "_selectedSectorId"));

        sink.Calls.Clear();
        Call(game, "RunStarmapHover", true, (short)2, (short?)null);   // same cluster again — no repeat
        Assert.Empty(sink.Calls);

        Call(game, "RunStarmapHover", false, null, (short?)null);      // off-map — silent
        Assert.Empty(sink.Calls);
    }

    // ── P764b — the sink-only drain must not throw on non-sink events ──────

    [Fact]
    public void SoundEventDrain_WithOnlyTheSinkOverride_NonSinkEventsDoNotThrow()
    {
        // P764b — UpdateSoundEvents guarded with `var snd = _sound!` when only
        // the test override is installed: a queued non-sink event (here
        // STATION_DESTROYED) hit snd.PlayStationEvent on null and threw. The
        // per-case `snd?.` keeps the drain a silent no-op instead.
        var sink = new RecordingUiSink();
        using var session = DockedWorld(out var world);
        world.Apply(new StationDestroyed { StationId = 42 });
        var game = Headless(session, sink);

        Call(game, "UpdateSoundEvents");

        Assert.Empty(sink.Calls);   // station events ride SoundManager, not the UI sink
    }

    private static string LastLine(ClientWorld world)
    {
        // PostSystemLine records a sender-NA ChatMessage into the transient
        // event log; the newest entry is the line the trigger just posted.
        var chats = world.Events.Chats;
        return chats[^1].Message;
    }

    // ── P764b — the turret teleport branch, pinned on the wire ─────────────

    /// <summary>Minimal in-process loopback site (the P748 pattern): records
    /// every packet the session sends so the wire ORDER can be asserted.</summary>
    private sealed class SendSite : ITransportSite
    {
        public readonly TaskCompletionSource<ConnectionId> Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<byte[]> Received = new();
        public void OnPeerJoined(ConnectionId id, string name) => Joined.TrySetResult(id);
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId id, ReadOnlySpan<byte> bytes)
        { foreach (var message in FedPacket.Unpack(bytes)) Received.Enqueue(message); }
    }

    private static async Task UntilAsync(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!done()) await Task.Delay(5, timeout.Token);
    }

    [Collection("LiveNetwork")]
    public class TurretTeleportWireTests
    {
        private const BindingFlags HiddenFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static;

        [Fact]
        public async Task TeleportFromATurret_DisembarksFirst_ThenSendsDocked()
        {
            // P764b — native AttemptTeleportTo's turret branch
            // (sectormap.cpp:1369-1374): a ship with a ParentShip disembarks
            // (DisembarkAndTeleport) before the DOCKED fm — the port sends
            // BOARD_SHIP(NA) first, exactly like ActivateTeleport's station
            // branch (Game1.Panes.cs).
            var site = new SendSite();
            var host = new TcpTransport(site);
            using var session = new ConnectionSession();
            host.Host(0);
            session.Connect(host.BoundPort, "P764b turret teleport");
            await site.Joined.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var sink = new RecordingUiSink();
                var w = session.World;
                w.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(10, 0, default, 0, Vector3.Zero,
                    Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
                w.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "p764" });
                w.Apply(new ExportShip { ShipId = -1, SideId = 0 });
                w.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, StationId = 42, ParentId = 7,
                    State = ShipStatusMessage.StateDocked });
                w.Apply(new ExportStation { Station = Station(43, cluster: 2) });
                var game = Headless(session, sink);
                Set(game, "_teleportActive", true);

                Call(game, "RunStarmapClick", (short)2, false);

                await UntilAsync(() => site.Received.Any(b => FedMessageHeader.Read(b).Fmid == Docked.Fmid));
                var received = site.Received.ToArray();
                var boardIdx = Array.FindIndex(received, b => FedMessageHeader.Read(b).Fmid == BoardShip.Fmid);
                var dockedIdx = Array.FindIndex(received, b => FedMessageHeader.Read(b).Fmid == Docked.Fmid);
                Assert.True(boardIdx >= 0, "the turret branch must disembark (BOARD_SHIP NA) first");
                Assert.True(dockedIdx > boardIdx, "DOCKED must follow the disembark");
                Assert.Equal(-1, BoardShip.Decode(received.Single(b => FedMessageHeader.Read(b).Fmid == BoardShip.Fmid)).SidParent);
                Assert.Equal(43, Docked.Decode(received.Single(b => FedMessageHeader.Read(b).Fmid == Docked.Fmid)).StationId);
                Assert.False(Get<bool>(game, "_teleportActive"));
            }
            finally { host.Shutdown(); }
        }
    }

}

/// <summary>
/// P764 — the shop model's custom-loadout SLOTS (native
/// <c>m_customLoadouts[num]</c>, <c>clintlib.h:1155</c>,
/// <c>MAX_CUSTOM_LOADOUTS = 10</c> at clintlib.h:825): save stores the pending
/// loadout per (hull, slot); restore replaces the pending loadout wholesale.
/// P764b — a NEVER-SAVED slot restores the hull's DEFAULT loadout, not an
/// empty hold: RefreshLoadout terminates every current part and then hands
/// the (absent) cached list to ReplaceLoadout, which buys the default when
/// there is no cached loadout link for that hull (clintlib.h:1796-1800). Only
/// a slot the player saved EMPTY restores empty.
/// </summary>
public sealed class P764CustomLoadoutSlotTests
{
    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SaveRestoreRoundTrip_PerHullAndSlot_NeverSavedSlotRestoresTheDefault()
    {
        string root = ArtRoot;
        if (root.Length == 0 || !File.Exists(Path.Combine(root, "PCore001.igc")))
            return;   // corpus absent on a clean clone (P597)
        var catalog = ShopCatalog.TryLoad(root) ?? throw new InvalidOperationException("catalogue missing");
        var hull = catalog.Hulls.OrderBy(h => h.HullId).First();
        var weapon = catalog.Parts.First(p => p.EquipmentType == EquipmentType.Weapon);
        var model = new ShopModel(catalog, currentHullId: hull.HullId,
            currentParts: new[] { new ExpandedPartData(weapon.PartId, 1, 0, ExpandedPartData.FullyMounted) });
        Assert.Single(model.ChosenParts);

        // The expected default: a fresh model on the same hull after
        // ApplyDefaultLoadout (native BuyDefaultLoadout, clintlib.h:2110-2160).
        var expectedDefault = new ShopModel(catalog, currentHullId: hull.HullId,
            currentParts: Array.Empty<ExpandedPartData>());
        expectedDefault.ApplyDefaultLoadout();

        // Save slot 2, restore the never-saved slot 9: the DEFAULT loadout
        // comes back (P764b, clintlib.h:1796-1800) — not an empty hold.
        model.SaveCustomLoadoutToSlot(2);
        model.RestoreCustomLoadout(9);
        Assert.Equal(expectedDefault.ChosenParts, model.ChosenParts);

        // Restoring slot 2 brings the saved list back wholesale.
        model.RestoreCustomLoadout(2);
        Assert.Single(model.ChosenParts);
        Assert.Equal(weapon.PartId, model.ChosenParts[0].PartId);

        // A slot the player saved EMPTY restores empty (the only empty case): a
        // model whose pending loadout is empty saves an empty list for the
        // hull, and restoring that slot terminates everything and applies the
        // (empty) cached list — native ReplaceLoadout with a found link
        // (clintlib.h:1797-1798).
        var emptyModel = new ShopModel(catalog, currentHullId: hull.HullId,
            currentParts: Array.Empty<ExpandedPartData>());
        emptyModel.SaveCustomLoadoutToSlot(7);
        emptyModel.RestoreCustomLoadout(7);
        Assert.Empty(emptyModel.ChosenParts);

        // Slot 0 stays what P759's BuyHull rebase reads — SaveCustomLoadout()
        // is slot 0 (load.cpp:1521/1646).
        model.SaveCustomLoadout();
        model.RestoreCustomLoadout(9);
        model.RestoreCustomLoadout(0);
        Assert.Single(model.ChosenParts);
        Assert.Equal(weapon.PartId, model.ChosenParts[0].PartId);
    }
}
