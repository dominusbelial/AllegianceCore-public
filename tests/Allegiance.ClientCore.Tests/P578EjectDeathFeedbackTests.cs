using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P578 — the eject/death/rescue feedback layer: the pure text/routing rules of
/// <see cref="EjectFeedback"/> (a headless port of native appmsg.cpp:2797-2844
/// and 311-340) and the <see cref="ClientWorld"/> consumption of KILL_SHIP(105) /
/// PLAYER_RESCUED(16) — which feedback line is queued for each receiver
/// perspective, whether an eject's ship stays alive (bDeathCredit=false) versus
/// a true death being marked dead, and the camera-override request for one's own
/// eject/kill. Failing-first: every behavior asserted here did not exist before
/// P578 (no tip lines, Apply(KillShip) marked every victim dead, PLAYER_RESCUED
/// was ignored).
/// </summary>
public class P578EjectDeathFeedbackTests
{
    private const short Me = 4400;
    private const short Killer = 9901;

    private static KillShip Msg(short victim, bool deathCredit, short idCredit = -1) => new()
    {
        ShipId = victim, TypeCredit = 0, IdCredit = idCredit, SideIdKiller = -1,
        BKillCredit = idCredit != -1, BDeathCredit = deathCredit,
    };

    private static ClientWorld WorldWithMe()
    {
        var w = new ClientWorld();
        w.Apply(new SetCluster
        {
            SectorId = 3,
            Ship = ServerShipUpdate.FromModel(Me, MakeShip(Me), 1u),
            Cookie = 0x11,
        });
        w.Apply(new ExportShip { ShipId = Killer, HullId = 601, SideId = 902, CharacterName = "Bandit" });
        w.Apply(new ExportShip { ShipId = 7777, HullId = 602, SideId = 901, CharacterName = "Victim" });
        return w;
    }

    private static Ship MakeShip(short id) =>
        new(id, TestHull(), new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity);

    private static DataHullTypeIGC TestHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 603, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 611f, HardpointOffset: 630,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    // ── Pure text/routing (EjectFeedback.ForKillShip) ────────────────────

    [Theory]
    [InlineData(true, false, "You were killed")]
    [InlineData(false, false, "You ejected")]
    [InlineData(true, true, "You were killed by Bandit")]
    [InlineData(false, true, "You were forced to eject by Bandit")]
    public void Self_VictimText_IsCriticalAndMatched(bool death, bool withKiller, string expected)
    {
        var line = EjectFeedback.ForKillShip(Msg(Me, death, withKiller ? Killer : (short)-1), Me, "Me", withKiller ? "Bandit" : null);
        Assert.Equal(expected, line.Text);
        Assert.True(line.IsCritical, "a line about the receiver's own ship must be critical (appmsg.cpp:2812,2834)");
    }

    [Theory]
    [InlineData(true, "You killed Victim")]
    [InlineData(false, "You forced Victim to eject")]
    public void Self_IsKiller_TextIsCritical(bool death, string expected)
    {
        // IdCredit (the credited killer) is me; the victim is someone else.
        var m = Msg(7777, death, idCredit: Me);
        var line = EjectFeedback.ForKillShip(m, Me, "Victim", "Me");
        Assert.Equal(expected, line.Text);
        Assert.True(line.IsCritical, "a kill YOU got is critical (appmsg.cpp:2838-2842)");
    }

    [Theory]
    [InlineData(true, "Victim was killed")]
    [InlineData(false, "Victim ejected")]
    public void Other_Observer_IsNonCritical(bool death, string expected)
    {
        var line = EjectFeedback.ForKillShip(Msg(7777, death, idCredit: Killer), Me, "Victim", "Bandit");
        Assert.Equal(expected, line.Text);
        Assert.False(line.IsCritical, "an observer line for another player is non-critical (appmsg.cpp:2801-2810)");
    }

    // ── Pure rescue routing (EjectFeedback.ForPlayerRescued) ─────────────

    [Theory]
    [InlineData(IgcObjectType.Station, false, "Victim ejected")]
    [InlineData(IgcObjectType.Probe, false, "Victim ejected")]
    [InlineData(IgcObjectType.Mine, false, "Victim ejected")]
    [InlineData(IgcObjectType.Station, true, "Victim was killed")]
    public void Non_ship_killer_id_matching_me_still_uses_observer_text(IgcObjectType type, bool death, string expected)
    {
        var w = WorldWithMe();
        // Object ids can overlap across types. A station with my ship id is
        // not my ship and must not claim "You forced/killed Victim".
        var m = Msg(7777, death, idCredit: Me);
        m.TypeCredit = (short)type;
        w.Apply(m);
        Assert.Equal(expected, Assert.Single(w.FeedbackLines).Text);
        Assert.Equal(death, w.Ships[7777].IsDead);
        Assert.Empty(w.TakeCameraOverrideRequests());
    }

    [Theory]
    [InlineData(IgcObjectType.Station, false, "You ejected")]
    [InlineData(IgcObjectType.Probe, false, "You ejected")]
    [InlineData(IgcObjectType.Mine, true, "You were killed")]
    public void ApplyKillShip_Non_ship_killer_does_not_borrow_a_ship_name(IgcObjectType type, bool death, string expected)
    {
        var w = WorldWithMe();
        var m = Msg(Me, death, idCredit: Killer);
        m.TypeCredit = (short)type; // same id as the known ship "Bandit"
        w.Apply(m);
        Assert.Equal(expected, Assert.Single(w.FeedbackLines).Text);
        Assert.Equal(death, w.Ships[Me].IsDead);
        Assert.Empty(w.TakeCameraOverrideRequests());
    }

    [Fact]
    public void Rescue_IAmRescuer_NonCriticalWithRescueCue()
    {
        var line = EjectFeedback.ForPlayerRescued(
            new PlayerRescued { ShipIdRescuer = Me, ShipIdRescuee = 555 }, Me, "Me", "Poddy");
        Assert.Equal("You rescued Poddy.", line!.Value.Text);
        Assert.False(line.Value.IsCritical);                       // appmsg.cpp:324
        Assert.Equal(FeedbackExtraSound.RescuePlayer, line.Value.Extra);  // :325
    }

    [Fact]
    public void Rescue_IAmRescuee_CriticalWithJumpCue()
    {
        var line = EjectFeedback.ForPlayerRescued(
            new PlayerRescued { ShipIdRescuer = 888, ShipIdRescuee = Me }, Me, "Savior", "Me");
        Assert.Equal("Savior rescued you.", line!.Value.Text);
        Assert.True(line.Value.IsCritical);                        // appmsg.cpp:337-338
        Assert.Equal(FeedbackExtraSound.Jump, line.Value.Extra);   // :339
    }

    [Fact]
    public void Rescue_NeitherEndpoint_NoLine()
    {
        var line = EjectFeedback.ForPlayerRescued(
            new PlayerRescued { ShipIdRescuer = 701, ShipIdRescuee = 889 }, Me, "A", "B");
        Assert.Null(line);
    }

    // ── ClientWorld consumption ──────────────────────────────────────────

    [Fact]
    public void ApplyKillShip_TrueDeath_MarksDeadAndQueuesSelfCriticalAndOverride()
    {
        var w = WorldWithMe();
        var m = Msg(Me, deathCredit: true, idCredit: Killer);
        w.Apply(m);

        Assert.True(w.Ships[Me].IsDead, "a true death (bDeathCredit) must mark the victim dead");
        var lines = w.TakeFeedbackLines();
        Assert.Single(lines);
        Assert.Equal("You were killed by Bandit", lines[0].Text);
        Assert.True(lines[0].IsCritical);

        var overs = w.TakeCameraOverrideRequests();
        Assert.Single(overs);
        Assert.Equal(Killer, overs[0].FocusShipId);
    }

    [Fact]
    public void ApplyKillShip_Eject_DoesNotMarkDead_QueuesForcedEjectCritical()
    {
        var w = WorldWithMe();
        var m = Msg(Me, deathCredit: false, idCredit: Killer);   // forced eject, not a death
        w.Apply(m);

        Assert.False(w.Ships[Me].IsDead, "an eject (bDeathCredit=false) must NOT mark the victim dead — the pod survives");
        var lines = w.TakeFeedbackLines();
        Assert.Single(lines);
        Assert.Equal("You were forced to eject by Bandit", lines[0].Text);
        Assert.True(lines[0].IsCritical);

        // And the camera swing to the killer is requested.
        var overs = w.TakeCameraOverrideRequests();
        Assert.Single(overs);
        Assert.Equal(Killer, overs[0].FocusShipId);
    }

    [Fact]
    public void ApplyKillShip_Observer_NonCriticalNoOverride()
    {
        var w = WorldWithMe();
        w.Apply(Msg(7777, deathCredit: true, idCredit: Killer));
        var lines = w.TakeFeedbackLines();
        Assert.Single(lines);
        Assert.Equal("Victim was killed", lines[0].Text);
        Assert.False(lines[0].IsCritical);
        Assert.Empty(w.TakeCameraOverrideRequests());   // not my ship — no gloat camera
    }

    [Fact]
    public void ApplyPlayerRescued_QueuesRescueLineForReceiver()
    {
        var w = WorldWithMe();
        // I am the rescuee.
        w.Apply(new PlayerRescued { ShipIdRescuer = Killer, ShipIdRescuee = Me });
        var lines = w.TakeFeedbackLines();
        Assert.Single(lines);
        Assert.Equal("Bandit rescued you.", lines[0].Text);
        Assert.True(lines[0].IsCritical);
    }

    [Fact]
    public void ApplyPlayerRescued_NeitherEndpoint_NoLine()
    {
        var w = WorldWithMe();
        w.Apply(new PlayerRescued { ShipIdRescuer = 710, ShipIdRescuee = 820 });
        Assert.Empty(w.TakeFeedbackLines());
    }
}
