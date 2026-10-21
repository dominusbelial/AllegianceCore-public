using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using SimVec = Allegiance.Core.Collision.Vector3;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-12b — the review-fix round on HUD-12 plus the HUD-11 leftovers:
/// <list type="bullet">
/// <item>fix 1 — the per-frame HUD draw path allocates nothing: the hostile
/// damage bucket prune reuses a scratch list, and every formatted readout is
/// cached by its integer value (<see cref="HudStringCache"/>);</item>
/// <item>fix 3 — <c>OWN_SHIP_CLOAK_UPDATE(220)</c> applies to
/// <see cref="ClientWorld.MyCloaking"/>;</item>
/// <item>fix 4 — the friendly ripcord countdown data path starts on
/// <c>RIPCORD_ACTIVATE(185)</c> and clears on abort/kill/delete;</item>
/// <item>fix 5 — <c>CLOAK DRAINED</c> still shows before the first 220;</item>
/// <item>fix 6 — the damage-bucket flash anchor is pruned with its bucket;</item>
/// <item>fixes 8/9/10 — native <c>c_cidNone</c> sends, the SpriteFont
/// <c>FitFontScale</c> overload and the cached chat-line clip.</item>
/// </list>
/// </summary>
public class Hud12bReviewFixTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public Hud12bReviewFixTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    // ── fix 1: no per-frame allocations ────────────────────────────────────

    [Fact]
    public void Hostile_damage_threat_and_flash_allocate_nothing_per_contact()
    {
        var tracker = new Game1.HostileDamageTracker();
        const long now = 40_000;
        tracker.Record(7, 25f, now);
        Assert.True(tracker.Threat(7, now));

        for (int i = 0; i < 1000; i++) { tracker.Threat(7, now); tracker.Flash(7, now); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            tracker.Threat(7, now);
            tracker.Flash(7, now);
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // Before HUD-12b Prune built slots.Keys.Where(...).ToList() on every
        // Threat() call — i.e. once per radar ship per frame.
        _output.WriteLine($"HUD12b Threat+Flash allocation over 10k calls: {bytes} B");
        Assert.True(bytes < 1024, $"Threat/Flash allocated {bytes} B over 10,000 calls");
    }

    [Fact]
    public void HUD_formatted_readouts_are_cached_by_integer_value()
    {
        Assert.Same(HudStringCache.Lives(5), HudStringCache.Lives(5));
        Assert.Equal("5 lives", HudStringCache.Lives(5));
        Assert.Equal("4 kills", HudStringCache.Kills(4));
        Assert.Equal("CLOAK 22%", HudStringCache.Cloak(22.4f));
        Assert.Equal("CLOAK 45% DRAINED", HudStringCache.CloakDrained(44.6f));
        Assert.Equal("50%", HudStringCache.Percent(.5f));
        Assert.Equal("SIG: 50%", HudStringCache.Signature(50f));
        Assert.Equal("Kill bonus: 25%", HudStringCache.KillBonusLine(25f));
        Assert.Equal("KB 50%", HudStringCache.KillBonusBadge(50f));
        Assert.Equal("50", HudStringCache.KillBonusNumber(50f));
        Assert.Equal("1350 cr", HudStringCache.Money(1350));
        Assert.Equal("Speed: 179 mps", HudStringCache.Speed(179.2f, "mps"));
        Assert.Equal("1350 cr  AP off  RADAR Default",
            HudStringCache.SummaryLine(1350, false, RadarMode.Default));
        Assert.Equal("cr --  AP on  RADAR All", HudStringCache.SummaryLine(null, true, RadarMode.All));
        Assert.Equal("Ship: Adv Scout", HudStringCache.ShipSummary("Adv Scout"));
        Assert.Equal("Target: bandit", HudStringCache.TargetSummary("bandit"));
        Assert.Equal("Adv Scout (turret)", HudStringCache.TurretShipType("Adv Scout"));

        // Every helper returns the SAME instance for the same underlying value.
        Assert.Same(HudStringCache.Kills(4), HudStringCache.Kills(4));
        Assert.Same(HudStringCache.Cloak(22.4f), HudStringCache.Cloak(22.4f));
        Assert.Same(HudStringCache.CloakDrained(44.6f), HudStringCache.CloakDrained(44.6f));
        Assert.Same(HudStringCache.Percent(.5f), HudStringCache.Percent(.5f));
        Assert.Same(HudStringCache.Signature(50f), HudStringCache.Signature(50f));
        Assert.Same(HudStringCache.KillBonusLine(25f), HudStringCache.KillBonusLine(25f));
        Assert.Same(HudStringCache.KillBonusBadge(50f), HudStringCache.KillBonusBadge(50f));
        Assert.Same(HudStringCache.KillBonusNumber(50f), HudStringCache.KillBonusNumber(50f));
        Assert.Same(HudStringCache.Money(1350), HudStringCache.Money(1350));
        Assert.Same(HudStringCache.Speed(179.2f, "mps"), HudStringCache.Speed(179.2f, "mps"));
        Assert.Same(HudStringCache.SummaryLine(1350, false, RadarMode.Default),
            HudStringCache.SummaryLine(1350, false, RadarMode.Default));
        Assert.Same(HudStringCache.ShipSummary("Adv Scout"), HudStringCache.ShipSummary("Adv Scout"));
        Assert.Same(HudStringCache.TargetSummary("bandit"), HudStringCache.TargetSummary("bandit"));
        Assert.Same(HudStringCache.TurretShipType("Adv Scout"), HudStringCache.TurretShipType("Adv Scout"));
    }

    [Fact]
    public void HUD_formatted_readouts_allocate_nothing_in_steady_state()
    {
        for (int i = 0; i < 200; i++) FormatFrame();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) FormatFrame();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        _output.WriteLine($"HUD12b formatted-readout allocation over 10k frames: {bytes} B");
        Assert.True(bytes < 1024, $"formatted HUD readouts allocated {bytes} B over 10,000 frames");

        static void FormatFrame()
        {
            _ = HudStringCache.Lives(5);
            _ = HudStringCache.Kills(4);
            _ = HudStringCache.Cloak(22.4f);
            _ = HudStringCache.CloakDrained(44.6f);
            _ = HudStringCache.Percent(.5f);
            _ = HudStringCache.Signature(50f);
            _ = HudStringCache.KillBonusLine(25f);
            _ = HudStringCache.KillBonusBadge(50f);
            _ = HudStringCache.KillBonusNumber(50f);
            _ = HudStringCache.Money(1350);
            _ = HudStringCache.Speed(179.2f, "mps");
            _ = HudStringCache.SummaryLine(1350, false, RadarMode.Default);
            _ = HudStringCache.ShipSummary("Adv Scout");
            _ = HudStringCache.TargetSummary("bandit");
            _ = HudStringCache.TurretShipType("Adv Scout");
            _ = HudStringCache.CloakStatusText(true, 45f);
        }
    }

    private static HudContact Contact(int id) => new(
        HudContactKind.Ship, new HudPoint(id, id), true, 1f, null, false, false,
        Name: $"ship-{id}", Range: 1200 + id, IsSameSide: true);

    private static long BytesPerHeadlessHudFrame(int count)
    {
        var contacts = new HudContact[count];
        for (int i = 0; i < count; i++) contacts[i] = Contact(i);
        var buffers = new HudSnapshotBuffers();
        var context = new HudContext { Contacts = contacts };
        var tracker = new Game1.HostileDamageTracker();
        const long now = 100_000;
        for (short i = 0; i < count; i++) tracker.Record(i, 1f, now);

        static void Frame(HudContext context, HudSnapshotBuffers buffers, Game1.HostileDamageTracker tracker,
            int count, long now)
        {
            var model = HudModel.Create(null, context, buffers);
            _ = model.Context.Contacts.Count;
            for (short i = 0; i < count; i++)
            {
                _ = tracker.Threat(i, now);
                _ = tracker.Flash(i, now);
            }
            _ = HudStringCache.Cloak(22.4f);
            _ = HudStringCache.Lives(5);
            _ = HudStringCache.SummaryLine(1350, false, RadarMode.Default);
        }

        for (int warm = 0; warm < 40; warm++) Frame(context, buffers, tracker, count, now);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 200; frame++) Frame(context, buffers, tracker, count, now);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / 200;
    }

    [Fact]
    public void Headless_HUD_frame_allocation_does_not_scale_with_contact_count()
    {
        long small = BytesPerHeadlessHudFrame(4);
        long large = BytesPerHeadlessHudFrame(400);

        // HUD-11 pinned the contact snapshot buffer. HUD-12b pins the remaining
        // per-contact work in the same frame: before the fix each of the 400
        // Threat() calls built a fresh List from slots.Keys.Where(...).ToList().
        _output.WriteLine($"HUD12b headless HUD allocation/frame: 4 contacts {small} B, 400 contacts {large} B");
        Assert.True(large < small + 4096,
            $"headless HUD frame allocation scaled with contacts: 4 -> {small} B, 400 -> {large} B");
    }

    // ── fix 6: the flash anchor is pruned with its bucket ──────────────────

    [Fact]
    public void Damage_bucket_flash_anchor_is_dropped_when_the_bucket_leaves_the_window()
    {
        var tracker = new Game1.HostileDamageTracker();
        const long t0 = 10_000;
        tracker.Record(7, 25f, t0);
        Assert.Equal(1, tracker.FlashAnchorCount);
        Assert.True(tracker.Threat(7, t0));

        // 8 × 5 s slots: 41 s later the bucket is empty and Prune removes the
        // attacker; its flash anchor must go with it (no unbounded leak).
        Assert.False(tracker.Threat(7, t0 + 41_000));
        Assert.Equal(0, tracker.FlashAnchorCount);
    }

    // ── fix 3: the cloak row's data path ───────────────────────────────────

    [Fact]
    public void OwnShipCloakUpdate_applies_the_native_visible_fraction_to_the_client_world()
    {
        var world = new ClientWorld();
        Assert.Null(world.MyCloaking);

        world.Apply(new OwnShipCloakUpdate { Cloaking = 0.35f });
        Assert.Equal(0.35f, world.MyCloaking!.Value, 5);
        Assert.Equal(65f, OwnShipCloakUpdate.StrengthPercent(world.MyCloaking.Value), 4);
        Assert.True(OwnShipCloakUpdate.IsCloaked(world.MyCloaking.Value));

        // A later tick overwrites wholesale (the native local sim value).
        world.Apply(new OwnShipCloakUpdate { Cloaking = 1f });
        Assert.Equal(1f, world.MyCloaking!.Value, 5);
        Assert.False(OwnShipCloakUpdate.IsCloaked(world.MyCloaking.Value));
    }

    // ── fix 5: CLOAK DRAINED before the first 220 ──────────────────────────

    private static HudFrame FrameWithCloak(float? strength, bool engagedBit, float energy = 1f) =>
        new(1f, 1f, 1f, 1f, energy, 1f, 100f, 0f, false, false, false, 0,
            Flashing: false, WarpFlash: 0f, Mining: false, Cloaked: engagedBit,
            KillBonusPercent: 0f, KillBonusStreak: 0, RollDegrees: 0f,
            WeaponSystems: Array.Empty<WeaponStatusRow>(), IsAdriftLifepod: false,
            OxygenSecondsRemaining: 0, CloakStrengthPercent: strength);

    [Fact]
    public void Cloak_drained_still_shows_before_the_first_cloak_strength_message()
    {
        var fallback = HudModel.Create(FrameWithCloak(strength: null, engagedBit: true, energy: 0.15f));
        Assert.True(fallback.Status.CloakActive);
        Assert.True(fallback.Status.CloakDrained);
        Assert.Null(fallback.Status.CloakStrengthPercent);
        // The old branch must still draw: no strength value, but drained.
        Assert.Equal("CLOAK DRAINED", HudStringCache.CloakStatusText(
            fallback.Status.CloakDrained, fallback.Status.CloakStrengthPercent));

        // Once the stream lands, the same state shows the native number.
        Assert.Equal("CLOAK 45% DRAINED", HudStringCache.CloakStatusText(true, 45f));
        Assert.Equal("CLOAK 45%", HudStringCache.CloakStatusText(false, 45f));
        // Not drained + unknown strength: native has nothing to draw.
        Assert.Null(HudStringCache.CloakStatusText(false, null));
    }

    // ── fix 4: the friendly ripcord data path ──────────────────────────────

    private static ServerShipUpdate PoseFor(short id) =>
        ServerShipUpdate.FromValues(
            id, stateM: 0, new ControlData(0f, 0f, 0f, 1f), time: 1u,
            new SimVec(id, 0f, 0f), Orientation.Identity, SimVec.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 1f);

    private static ClientWorld WorldWithRemoteShip(short remoteId)
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = PoseFor(5) });
        world.Apply(new SingleShipUpdate { OidTarget = remoteId, Ship = PoseFor(remoteId) });
        world.Apply(new PlayerInfo
        {
            CharacterName = "wing", ISide = 1, ShipId = remoteId, PilotType = PlayerInfo.PilotTypePlayer,
        });
        return world;
    }

    private static RipcordActivate Ripcord(short shipId) => new()
    {
        ShipidRipcord = shipId, OtRipcord = (short)IgcObjectType.Station,
        OidRipcord = 100, SidRipcord = 2,
    };

    [Fact]
    public void Remote_ripcord_seconds_start_on_activate_and_clear_on_abort_kill_and_delete()
    {
        const short remote = 9;
        var world = WorldWithRemoteShip(remote);
        long before = (long)(world.NowSeconds() * 1000);

        world.Apply(Ripcord(remote));
        long after = (long)(world.NowSeconds() * 1000);
        Assert.True(world.TryGetRemoteRipcord(remote, out long started));
        Assert.InRange(started, before, after);   // the activation's local start time

        world.Apply(new RipcordAborted { ShipidRipcord = remote });
        Assert.False(world.TryGetRemoteRipcord(remote, out _));

        world.Apply(Ripcord(remote));
        Assert.True(world.TryGetRemoteRipcord(remote, out _));
        world.Apply(new KillShip
        {
            ShipId = remote, TypeCredit = (short)IgcObjectType.Ship, IdCredit = 5,
            SideIdKiller = 1, BKillCredit = true, BDeathCredit = true,
        });
        Assert.False(world.TryGetRemoteRipcord(remote, out _));

        world.Apply(Ripcord(remote));
        Assert.True(world.TryGetRemoteRipcord(remote, out _));
        world.Apply(ShipDelete.ForKilledShip(remote));
        Assert.False(world.TryGetRemoteRipcord(remote, out _));
    }

    // ── fix 8: native c_cidNone sends (HUD-11 leftovers) ──────────────────

    private static Game1.CommandTargetProfile Profile(Game1.TargetKind kind, short id, bool? friendly) =>
        new(kind, id, friendly, TargetHullAbm: null, StationSabm: null, ProbeEabm: null);

    [Fact]
    public void Self_target_sends_c_cidNone_with_the_own_ship_like_native()
    {
        // Native TK_TargetSelf: SetTarget(GetShip(), c_cidNone)
        // (WinTrek.cpp:10440-10443). c_cidNone is NOT c_cidDefault, so no default
        // order is resolved and the model is still sent.
        var self = new Game1.AutopilotTarget(Game1.TargetKind.Ship, 5, "me", UseDefault: false);
        var set = Game1.CurrentCommandSetFor(self, DataHullTypeIGC.HabmFighter,
            ownIsLayer: false, ownHasParent: false, Profile(Game1.TargetKind.Ship, 5, friendly: true));

        Assert.NotNull(set);
        Assert.Equal(OrderChange.CidNone, set!.Value.CommandId);
        Assert.Equal(IgcObjectType.Ship, set.Value.ObjectType);
        Assert.Equal((short)5, set.Value.ObjectId);

        // An explicit c_cidDefault still resolves even through the command-copy
        // path: native takes the c_cidDefault branch regardless of caller.
        var explicitDefault = new Game1.AutopilotTarget(
            Game1.TargetKind.Ship, 42, "enemy", OrderChange.CidDefault, UseDefault: false);
        var resolved = Game1.CurrentCommandSetFor(explicitDefault, DataHullTypeIGC.HabmFighter,
            ownIsLayer: false, ownHasParent: false, Profile(Game1.TargetKind.Ship, 42, friendly: false));
        Assert.Equal(OrderChange.CidAttack, resolved!.Value.CommandId);
        Assert.Equal((short)42, resolved.Value.ObjectId);
    }

    [Fact]
    public void TK_TargetCommand_preserves_the_accepted_slots_c_cidNone()
    {
        // Native TK_TargetCommand copies the accepted command id verbatim
        // (WinTrek.cpp:10408-10412); an accepted slot with c_cidNone stays
        // c_cidNone instead of becoming Defend-on-self.
        var world = WorldWithRemoteShip(9);
        var target = Game1.CommandSelection(world,
            new QueuedCommandState(OrderChange.CidNone, IgcObjectType.Ship, 9));

        Assert.NotNull(target);
        Assert.Equal(OrderChange.CidNone, target!.Value.CommandId);
        Assert.False(target.Value.UseDefault);

        var set = Game1.CurrentCommandSetFor(target, DataHullTypeIGC.HabmFighter,
            ownIsLayer: false, ownHasParent: false, Profile(Game1.TargetKind.Ship, 9, friendly: true));
        Assert.Equal(OrderChange.CidNone, set!.Value.CommandId);
        Assert.Equal((short)9, set.Value.ObjectId);
    }

    [Fact]
    public void SendCurrentCommand_wiring_records_the_exact_native_payload()
    {
        // Own ship → payload sent through a recording sink (the same send shape
        // SendCurrentCommandToServer passes to ConnectionSession.SendOrderChange).
        var sent = new List<(short ShipId, short CommandId, short Command, IgcObjectType ObjectType, short ObjectId)>();
        void Record(short shipId, short commandId, short command, IgcObjectType objectType, short objectId) =>
            sent.Add((shipId, commandId, command, objectType, objectId));

        var self = new Game1.AutopilotTarget(Game1.TargetKind.Ship, 5, "me", UseDefault: false);
        Game1.SendCurrentCommand(5, self, DataHullTypeIGC.HabmFighter, false, false,
            Profile(Game1.TargetKind.Ship, 5, friendly: true), Record);

        var only = Assert.Single(sent);
        Assert.Equal((short)5, only.ShipId);
        Assert.Equal(OrderChange.CidNone, only.CommandId);
        Assert.Equal(OrderChange.CmdCurrent, only.Command);
        Assert.Equal(IgcObjectType.Ship, only.ObjectType);
        Assert.Equal((short)5, only.ObjectId);

        // A real c_cidDefault pick still resolves the native default order.
        sent.Clear();
        var enemy = new Game1.AutopilotTarget(Game1.TargetKind.Ship, 42, "enemy");
        Game1.SendCurrentCommand(5, enemy, DataHullTypeIGC.HabmFighter, false, false,
            Profile(Game1.TargetKind.Ship, 42, friendly: false), Record);
        only = Assert.Single(sent);
        Assert.Equal(OrderChange.CidAttack, only.CommandId);
        Assert.Equal(IgcObjectType.Ship, only.ObjectType);
        Assert.Equal((short)42, only.ObjectId);

        // No target: native sends OT_invalid/NA with c_cidNone.
        sent.Clear();
        Game1.SendCurrentCommand(5, null, DataHullTypeIGC.HabmFighter, false, false,
            Profile(Game1.TargetKind.Ship, 42, friendly: false), Record);
        only = Assert.Single(sent);
        Assert.Equal(OrderChange.CidNone, only.CommandId);
        Assert.Equal((IgcObjectType)(-1), only.ObjectType);
        Assert.Equal((short)-1, only.ObjectId);
    }

    // ── fixes 9/10: no per-row closure / per-character truncation ──────────

    [Fact]
    public void Inventory_stats_fit_uses_the_shared_width_rule()
    {
        Assert.Equal(1f, InventoryPaneLayout.FitScale(50f, 100f));
        Assert.Equal(.5f, InventoryPaneLayout.FitScale(200f, 100f));
        Assert.Equal(1f, InventoryPaneLayout.FitScale(0f, 100f));
        Assert.Equal(1f, InventoryPaneLayout.FitFontScale("", 100f, Measure));
        Assert.Equal(.5f, InventoryPaneLayout.FitFontScale("long", 100f, Measure));
        Assert.Equal(1f, InventoryPaneLayout.FitFontScale("x", 100f, Measure));

        // HUD-11's delegate overload keeps its public contract.
        static float Measure(string text) => text.Length * 50f;
        Assert.Equal(.5f, InventoryPaneLayout.FitFontScale("long", 100f, Measure));
    }

    [Fact]
    public void Chat_line_truncation_matches_the_native_clip_and_is_cached_per_source_and_width()
    {
        static float Measure(string text) => text.Length * 13f;
        Func<string, float> measure = static t => t.Length * 13f;
        var cache = new ChatLineTruncationCache();
        string source = new string('x', 60);

        string first = cache.Fit(source, 200f, measure);
        Assert.EndsWith("...", first);
        Assert.True(Measure(first) <= 200f);
        Assert.True(Measure(first + "x") > 200f, "the clip must be the longest prefix, not a short one");
        Assert.Same(first, cache.Fit(source, 200f, measure));
        Assert.NotSame(first, cache.Fit(source, 120f, measure));

        for (int i = 0; i < 200; i++) cache.Fit(source, 200f, measure);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) cache.Fit(source, 200f, measure);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // The old renderer loop built rowText + "..." once per removed character
        // on every frame; the cache must return the same instance.
        _output.WriteLine($"HUD12b chat-clip allocation over 10k frames: {bytes} B");
        Assert.True(bytes < 1024, $"cached chat truncation allocated {bytes} B over 10,000 frames");
    }

    [Fact]
    public void HUD_renderers_do_not_allocate_per_row_or_per_character()
    {
        string hud = ReadSource("src/Allegiance.ClientCore.Game/Rendering/HudRenderer.cs");
        Assert.DoesNotContain("FitFontScale(stats, statsMax, t =>", hud);
        Assert.Contains("FitFontScale(stats, statsMax, rowFont, rowFs", hud);   // FONT-1 — SmallFont bake, no closure

        string modern = ReadSource("src/Allegiance.ClientCore.Game/Rendering/ModernHudRenderer.cs");
        Assert.DoesNotContain("t => _small.MeasureString(t).X", modern);
        Assert.Contains("FitFontScale(stats, statsMax, _small, _scale)", modern);
        Assert.DoesNotContain("rowText += \"...\"", modern);
        Assert.DoesNotContain("while (rowText.Length > 0", modern);
        Assert.Contains("_chatTruncations.Fit(rowText", modern);
    }

    private static string ReadSource(string relative)
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game", "Game1.cs")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root, relative));
    }
}
