using System.Reflection;
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
/// P454 — a real loopback probe deploy + ripcord, the port of the teleport-
/// probe ripcord tiers of <c>CshipIGC::FindRipcordModel</c> (<c>shipIGC.cpp:
/// 3803-3814, 3841-3896</c>) and the probe anchor's legality/landing
/// (<c>probeigc.h:248-254</c>, <c>FedSrv.CPP:13461-13476</c>). A loopback
/// player deploys a REAL PCore001 teleport probe (the <c>ripcordprobe</c> type
/// id 600: <c>DtRipcord = 15</c>, <c>Lifespan = 60</c>, <c>Radius = 10</c>)
/// through the real <c>FIRE_EXPENDABLE</c> path, flies to an anchor-less
/// sector, and ripcords back to the probe's sector.
///
/// Topology (same XXV_simple sectors the P111 live tests measure): the ripper
/// deploys in <see cref="DeploySector"/> 1052 (Borasisi — no station), then is
/// parked in the anchor-less <see cref="WaitSector"/> 1056 (Farout) to make the
/// request cross-sector, where 1052 has no friendly ripcord station to shadow
/// the probe. The ship is parked (kill-throttle <c>Throttle = -1</c>, the
/// <c>ExecuteMove</c> cruise=0 branch) so the long activation-delay pump does
/// not drift it into a warp/station.
///
/// Scenarios: the headline wire test (deploy → anchor → teleport lands within
/// probe.Radius + ship.Radius + 25), before-delay denial, non-teleport denial,
/// friendly-station-beats-probe (tier 2 &gt; tier 3), command-target-probe-
/// beats-station (tier 1 &gt; tier 2), most-recent-probe-wins (tier 3, no goal),
/// and probe-destroyed-mid-countdown abort.
/// </summary>
[Collection("LiveNetwork")]
public class LiveTeleportProbeRipcordTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private const short DeploySector = 1052;   // Borasisi — no station (probe-only target)
    private const short WaitSector = 1056;     // Farout — anchor-less, where the ripper parks
    private const int DelayPumpTicks = 330;    // ~16.5s > the ripcordprobe's 15s activation delay

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot, StaticCore = "PCore001.igc", Port = port, UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "ProbeRipcord Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 611u, DwCookie = 0x611u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 7799000000000000000ul, DrmHash = "rip",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Ship ServerShip(Srv server, short id) =>
        server.Ships.First(s => s.Ship.Id == id).Ship;

    private static void AddStationToSector(Sector sector, DataStationIGC station)
    {
        var mission = sector.Mission!;
        mission.PlaceStation(new Station(station, mission.Core.StationTypes[station.StationTypeId]), sector);
    }

    /// <summary>The REAL PCore001 teleport probe with the SMALLEST activation
    /// delay (DtRipcord >= 0) — the <c>ripcordprobe</c> id 600 (DtRipcord 15,
    /// Lifespan 60, Radius 10). The sibling ripcordprobe id 6 (DtRipcord 30,
    /// Lifespan 60) is deliberately avoided: with the ripper hull's
    /// <c>RipcordSpeed = 30</c>, <c>GetCanRipcord</c>'s expiry conjunct
    /// (<c>now + 31 &lt;= T0 + 60</c> → <c>now − T0 &lt;= 29</c>) contradicts the
    /// delay conjunct (<c>now − T0 &gt;= 30</c>), so id 6 is never ripcordable
    /// for this hull; id 600 has a genuine [15, 29] window.</summary>
    private static DataProbeTypeIGC TeleportType(StaticCore core)
    {
        var t = core.ProbeTypes.Values.Where(pt => pt.DtRipcord >= 0f).OrderBy(pt => pt.DtRipcord).FirstOrDefault();
        Assert.NotEqual(0, t.ExpendableTypeId);
        return t;
    }

    /// <summary>A real PCore001 NON-teleport probe (DtRipcord &lt; 0).</summary>
    private static DataProbeTypeIGC PlainType(StaticCore core)
    {
        var t = core.ProbeTypes.Values.FirstOrDefault(pt => pt.DtRipcord < 0f);
        Assert.NotEqual(0, t.ExpendableTypeId);
        return t;
    }

    private static void MountProbeLauncher(Ship ship, StaticCore core, DataProbeTypeIGC probeType, short amount)
    {
        var part = new DataPartTypeIGC(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "probe", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: 777, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.ProbeLauncher(probeType.ExpendableTypeId));
        // H-07/H-08: remount replaces the depleted launcher; do not leave
        // duplicate dispensers at slot zero and rely on excess old rounds.
        ship.TryRemovePart(EquipmentType.Dispenser, 0, out _);
        ship.MountPart(part, amount, mountId: 0, fractionMounted: 255);
    }

    private sealed class Ctx
    {
        public Ship Ripper = null!;
        public Mission Mission = null!;
        public Sector Deploy = null!, Wait = null!;
    }

    /// <summary>Boot a one-bot server: the ripper spawns flying on side 0 in
    /// <see cref="DeploySector"/> and is PARKED (kill-throttle) so it holds
    /// position through the long activation-delay pumps.</summary>
    private static (Srv server, FltBot bot, Ctx ctx) Setup(out short rid)
    {
        var server = new Srv();
        var ctx = new Ctx();
        server.ShipSpawner = (core, m, shipId) =>
        {
            ctx.Deploy = m.Sectors.First(s => s.Id == DeploySector);
            ctx.Wait = m.Sectors.First(s => s.Id == WaitSector);
            ctx.Mission = m;
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            Assert.False(hull.HasCapability(DataHullTypeIGC.HabmNoRipcord),
                $"test hull {hull.HullId} has c_habmNoRipcord — cannot exercise the handshake");
            ctx.Ripper = new Ship(shipId, hull, new Vector3(300f, 620f, 880f), Vector3.Zero, Orientation.Identity)
            { SideId = 0 };
            m.PlaceShip(ctx.Ripper, ctx.Deploy);
            ctx.Ripper.Controls = new ControlData(0f, 0f, 0f, -1f);   // kill-throttle: hold position (Ship.cs:5115)
            return (ctx.Ripper, ctx.Deploy);
        };
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass ready-up lobby

        var bot = new FltBot();
        bot.Connect(server.BoundPort, "PRIP");
        bot.SendLogonRequest(Request("PRIP"));
        PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        rid = bot.ShipAssignment!.Ship.ShipId;
        Assert.NotNull(ctx.Ripper);
        return (server, bot, ctx);
    }

    /// <summary>Deploy <paramref name="type"/> through the REAL FIRE_EXPENDABLE
    /// path (a mounted probe launcher + the wire request) in the ripper's
    /// deploy sector, and return the resulting live <see cref="Probe"/>.</summary>
    private static Probe DeployViaWire(Srv server, FltBot bot, Ship ship, Sector deploy, StaticCore core, DataProbeTypeIGC type)
    {
        MountProbeLauncher(ship, core, type, amount: 3);
        bot.SendFireExpendable(EquipmentType.Dispenser);
        int seen = PumpUntil(server,
            () => deploy.Models.OfType<Probe>().Any(p => p.ProbeType.ExpendableTypeId == type.ExpendableTypeId), 8000);
        Assert.True(seen < 8000, $"no probe {type.ExpendableTypeId} deployed via FIRE_EXPENDABLE");
        return deploy.Models.OfType<Probe>().Single(p => p.ProbeType.ExpendableTypeId == type.ExpendableTypeId);
    }

    /// <summary>Pump past the deployed probe's activation delay (DtRipcord
    /// seconds at 20 ups) plus a safety margin, so CanRipcord's
    /// <c>(now − DeployTime) &gt;= dt</c> conjunct holds — while staying inside
    /// the probe's lifespan ripcord window (a short margin, not a full
    /// <c>DelayPumpTicks</c>). The ship is parked (kill-throttle) so the pump
    /// does not drift it anywhere.</summary>
    private static void PumpPastActivationDelay(Srv server, Probe probe)
    {
        int ticks = (int)(probe.ProbeType.DtRipcord * 20) + 60;   // dt sec × 20 ups + 3s margin
        for (int i = 0; i < ticks; i++) server.TickSim();
    }

    /// <summary>The "fly to sector Y" leg — move the ripper into the anchor-less
    /// wait sector (out of the old sector's model list, into the new one), and
    /// sync the peer's sector snapshot via the server's own
    /// <c>UpdatePeerSectorSnapshot</c> (<c>Server.Docking.cs:1293</c>) so a
    /// ripcord first-activation broadcast (sector-wide to the ripper's sector)
    /// still reaches the moved ship.</summary>
    private static void MoveRipperToWait(Srv server, Ship ship, Mission mission, Sector deploy, Sector wait)
    {
        deploy.Remove(ship);
        mission.PlaceShip(ship, wait);
        var upd = typeof(Srv).GetMethod("UpdatePeerSectorSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        upd.Invoke(server, new object[] { ship, wait });
    }

    private static void ResetRipcord(FltBot bot)
    {
        bot.ResetRipcordActivates(); bot.ResetRipcordDenieds(); bot.ResetRipcordAborteds();
    }

    private static void NudgeCountdownTo(Ship ship, float v)
    {
        var field = typeof(Ship).GetField("_dtRipcordCountdown", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(ship, v);
    }

    /// <summary>⭐ THE headline wire test: a real teleport probe deployed through
    /// FIRE_EXPENDABLE in sector X becomes the ripcord anchor for a request made
    /// from a foreign sector Y (where X has no friendly station), and on
    /// countdown completion the ship lands in X within
    /// probe.Radius + ship.Radius + 25 of the probe. Must-fail pre-P454: no
    /// model is found (only the station and ship tiers existed), so the request
    /// is denied.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Wire_deployed_teleport_probe_is_the_anchor_and_ripcord_lands_near_it()
    {
        Require.Artwork();
        var (server, bot, ctx) = Setup(out short rid);
        ResetRipcord(bot);
        var core = ctx.Mission.Core;
        var tele = TeleportType(core);

        // (1) Deploy the real teleport probe through FIRE_EXPENDABLE in DeploySector.
        var probe = DeployViaWire(server, bot, ctx.Ripper, ctx.Deploy, core, tele);
        Assert.Equal(ctx.Ripper.SideId, probe.SideId);
        Assert.True(tele.DtRipcord >= 0f, "the probe must be a teleport type");
        Assert.Equal(ctx.Deploy.Id, probe.OwnerSector.Id);

        // (2) Fly to the anchor-less wait sector.
        MoveRipperToWait(server, ctx.Ripper, ctx.Mission, ctx.Deploy, ctx.Wait);
        Assert.Equal(WaitSector, ServerShip(server, rid).Sector!.Id);

        // (3) Let the probe's 15s activation delay elapse.
        PumpPastActivationDelay(server, probe);

        // (4) Ripcord to DeploySector → must anchor on the probe.
        bot.SendRipcordRequest(DeploySector);
        int seen = PumpUntil(server, () => bot.RipcordActivates.Count >= 1, 4000);
        Assert.True(seen < 4000, "no RIPCORD_ACTIVATE — the teleport probe was not chosen as the anchor");
        var act = Assert.Single(bot.RipcordActivates);
        Assert.Equal((short)IgcObjectType.Probe, act.OtRipcord);
        Assert.Equal(probe.Id, act.OidRipcord);
        Assert.Equal(DeploySector, act.SidRipcord);
        var rs = ServerShip(server, rid);
        Assert.True(rs.IsRipcordActive);
        Assert.Equal(new Ship.RipcordTarget((short)IgcObjectType.Probe, probe.Id, DeploySector), rs.RipcordModel);
        Assert.Empty(bot.RipcordDenieds);

        // (5) Complete the countdown → the ship lands in DeploySector within
        // probe.Radius + ship.Radius + 25 of the probe.
        NudgeCountdownTo(rs, 0.011f);
        server.TickSim();
        var after = ServerShip(server, rid);
        Assert.False(after.IsRipcordActive);
        Assert.Null(after.RipcordModel);
        Assert.Equal(DeploySector, after.Sector!.Id);
        float expectedR = probe.ProbeType.Radius + 0.5f * after.HullType.Length + 25.0f;
        float dist = (after.Position - probe.Position).Length();
        Assert.True(MathF.Abs(dist - expectedR) < 1.0f,
            $"probe-anchor distance {dist} != r {expectedR} (probe.Radius {probe.ProbeType.Radius} + ship.Radius {0.5f * after.HullType.Length} + 25)");

        Console.WriteLine($"PROBE-RIPCORD-LIVE: ship {rid} wire-deployed teleport probe {probe.Id} in {DeploySector}; ripcord from {WaitSector} -> anchored (ot=Probe oid={probe.Id}) + teleported to r={dist:F1} in {DeploySector}");
    }

    /// <summary>⭐ The same probe deployed through FIRE_EXPENDABLE is NOT chosen
    /// before its activation delay elapses (a request made immediately, while
    /// <c>(now − DeployTime) &lt; DtRipcord</c>, is denied).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_not_yet_active_is_denied_before_activation_delay()
    {
        Require.Artwork();
        var (server, bot, ctx) = Setup(out short rid);
        ResetRipcord(bot);
        var core = ctx.Mission.Core;
        var probe = DeployViaWire(server, bot, ctx.Ripper, ctx.Deploy, core, TeleportType(core));
        MoveRipperToWait(server, ctx.Ripper, ctx.Mission, ctx.Deploy, ctx.Wait);

        // No delay pump — request while still inside the 15s activation window.
        bot.SendRipcordRequest(DeploySector);
        int seen = PumpUntil(server, () => bot.RipcordDenieds.Count >= 1, 4000);
        Assert.True(seen < 4000, "expected RIPCORD_DENIED while the probe is still activating");
        Assert.Empty(bot.RipcordActivates);
        Assert.False(ServerShip(server, rid).IsRipcordActive);
        Assert.Null(ServerShip(server, rid).RipcordModel);

        Console.WriteLine($"PROBE-RIPCORD-LIVE: ship {rid} ripcord to {DeploySector} BEFORE probe {probe.Id} activation delay -> RIPCORD_DENIED (probe not CanRipcord)");
    }

    /// <summary>⭐ A NON-teleport probe (DtRipcord &lt; 0) is never a ripcord
    /// anchor even after ample time has passed — the request is denied.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Non_teleport_probe_is_denied_even_after_long_delay()
    {
        Require.Artwork();
        var (server, bot, ctx) = Setup(out short rid);
        ResetRipcord(bot);
        var core = ctx.Mission.Core;
        var probe = DeployViaWire(server, bot, ctx.Ripper, ctx.Deploy, core, PlainType(core));
        MoveRipperToWait(server, ctx.Ripper, ctx.Mission, ctx.Deploy, ctx.Wait);

        for (int i = 0; i < DelayPumpTicks; i++) server.TickSim();   // ample time; still not a teleport type

        bot.SendRipcordRequest(DeploySector);
        int seen = PumpUntil(server, () => bot.RipcordDenieds.Count >= 1, 4000);
        Assert.True(seen < 4000, "expected RIPCORD_DENIED for a non-teleport probe");
        Assert.Empty(bot.RipcordActivates);
        Assert.False(ServerShip(server, rid).IsRipcordActive);

        Console.WriteLine($"PROBE-RIPCORD-LIVE: ship {rid} ripcord to {DeploySector} with non-teleport probe {probe.Id} (DtRipcord {probe.ProbeType.DtRipcord}) -> RIPCORD_DENIED");
    }

    /// <summary>⭐ Tier ordering 2&gt;3: when BOTH a friendly ripcord station and
    /// a (non-command) teleport probe sit in the target sector, the STATION wins
    /// (the probe tier runs only after the station tier finds nothing).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Friendly_ripcord_station_wins_over_probe()
    {
        Require.Artwork();
        var (server, bot, ctx) = Setup(out short rid);
        ResetRipcord(bot);
        var core = ctx.Mission.Core;
        var probe = DeployViaWire(server, bot, ctx.Ripper, ctx.Deploy, core, TeleportType(core));

        var anchorType = core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmRipcord)).StationTypeId;
        var station = new DataStationIGC(
            Position: [900f, 0f, 0f], Up: [0f, 0f, 1f], Forward: [1f, 0f, 0f], Rotation: [1f, 0f, 0f, 0f],
            ClusterId: DeploySector, SideId: 0, StationId: 8290, StationTypeId: anchorType, BpHull: 1, BpShield: 1, Name: "Anch");
        AddStationToSector(ctx.Deploy, station);

        MoveRipperToWait(server, ctx.Ripper, ctx.Mission, ctx.Deploy, ctx.Wait);
        PumpPastActivationDelay(server, probe);

        bot.SendRipcordRequest(DeploySector);
        int seen = PumpUntil(server, () => bot.RipcordActivates.Count >= 1, 4000);
        Assert.True(seen < 4000, "no RIPCORD_ACTIVATE when a friendly station is present");
        var act = Assert.Single(bot.RipcordActivates);
        Assert.Equal((short)IgcObjectType.Station, act.OtRipcord);   // station beats the probe
        Assert.Equal(station.StationId, act.OidRipcord);

        Console.WriteLine($"PROBE-RIPCORD-LIVE: ship {rid} ripcord to {DeploySector} with station + probe {probe.Id} -> anchored on STATION {station.StationId} (tier 2 before tier 3)");
    }

    /// <summary>⭐ Tier ordering 1&gt;2: when the ripper's COMMAND TARGET is a
    /// ripcordable teleport probe, it beats a friendly ripcord station — the
    /// command-target probe tier runs before the station tier (shipIGC.cpp:
    /// 3803-3814 before 3835-3838). Also proves a Probe CAN be a command target
    /// (Ship.CommandTarget is a <see cref="Model"/>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Command_target_teleport_probe_beats_friendly_station()
    {
        Require.Artwork();
        var (server, bot, ctx) = Setup(out short rid);
        ResetRipcord(bot);
        var core = ctx.Mission.Core;
        var probe = DeployViaWire(server, bot, ctx.Ripper, ctx.Deploy, core, TeleportType(core));

        ctx.Ripper.CommandTarget = probe;   // c_cmdCurrent → a probe

        var anchorType = core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmRipcord)).StationTypeId;
        var station = new DataStationIGC(
            Position: [900f, 0f, 0f], Up: [0f, 0f, 1f], Forward: [1f, 0f, 0f], Rotation: [1f, 0f, 0f, 0f],
            ClusterId: DeploySector, SideId: 0, StationId: 8291, StationTypeId: anchorType, BpHull: 1, BpShield: 1, Name: "Anch");
        AddStationToSector(ctx.Deploy, station);

        MoveRipperToWait(server, ctx.Ripper, ctx.Mission, ctx.Deploy, ctx.Wait);
        PumpPastActivationDelay(server, probe);

        bot.SendRipcordRequest(DeploySector);
        int seen = PumpUntil(server, () => bot.RipcordActivates.Count >= 1, 4000);
        Assert.True(seen < 4000, "no RIPCORD_ACTIVATE for a command-target probe");
        var act = Assert.Single(bot.RipcordActivates);
        Assert.Equal((short)IgcObjectType.Probe, act.OtRipcord);   // the command-target probe beats the station
        Assert.Equal(probe.Id, act.OidRipcord);

        Console.WriteLine($"PROBE-RIPCORD-LIVE: ship {rid} command-target probe {probe.Id} in {DeploySector} beats friendly station {station.StationId} -> anchored on PROBE (tier 1 before tier 2)");
    }

    /// <summary>⭐ Tier 3, no goal: with TWO same-side teleport probes in the
    /// target sector and no command target, the MOST RECENTLY deployed wins
    /// (native searches the probe list backwards, shipIGC.cpp:3849/3874).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Most_recently_deployed_teleport_probe_wins_without_goal()
    {
        Require.Artwork();
        var (server, bot, ctx) = Setup(out short rid);
        ResetRipcord(bot);
        var core = ctx.Mission.Core;
        var tele = TeleportType(core);

        var p1 = DeployViaWire(server, bot, ctx.Ripper, ctx.Deploy, core, tele);
        // Deploy a SECOND teleport probe (remount + re-fire).
        MountProbeLauncher(ctx.Ripper, core, tele, amount: 3);
        bot.SendFireExpendable(EquipmentType.Dispenser);
        int seen = PumpUntil(server,
            () => ctx.Deploy.Models.OfType<Probe>().Count(p => p.ProbeType.ExpendableTypeId == tele.ExpendableTypeId) >= 2, 8000);
        Assert.True(seen < 8000, "second probe never deployed");
        var p2 = ctx.Deploy.Models.OfType<Probe>()
            .Where(p => p.ProbeType.ExpendableTypeId == tele.ExpendableTypeId)
            .OrderBy(p => p.DeployTime).Last();
        Assert.NotEqual(p1.Id, p2.Id);
        Assert.True(p2.DeployTime > p1.DeployTime);

        MoveRipperToWait(server, ctx.Ripper, ctx.Mission, ctx.Deploy, ctx.Wait);
        PumpPastActivationDelay(server, p2);

        bot.SendRipcordRequest(DeploySector);
        int seen2 = PumpUntil(server, () => bot.RipcordActivates.Count >= 1, 4000);
        Assert.True(seen2 < 4000, "no RIPCORD_ACTIVATE with two teleport probes");
        var act = Assert.Single(bot.RipcordActivates);
        Assert.Equal((short)IgcObjectType.Probe, act.OtRipcord);
        Assert.Equal(p2.Id, act.OidRipcord);   // the most recently deployed

        Console.WriteLine($"PROBE-RIPCORD-LIVE: ship {rid} two teleport probes ({p1.Id}@{p1.DeployTime:F2}, {p2.Id}@{p2.DeployTime:F2}) -> anchored on most-recent {p2.Id}");
    }

    /// <summary>⭐ A probe that vanishes mid-countdown (destroyed or expired —
    /// both remove it from the sector) aborts the ripcord: RIPCORD_ABORTED,
    /// state cleared, no teleport.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_destroyed_mid_countdown_aborts_ripcord()
    {
        Require.Artwork();
        var (server, bot, ctx) = Setup(out short rid);
        ResetRipcord(bot);
        var core = ctx.Mission.Core;
        var probe = DeployViaWire(server, bot, ctx.Ripper, ctx.Deploy, core, TeleportType(core));
        MoveRipperToWait(server, ctx.Ripper, ctx.Mission, ctx.Deploy, ctx.Wait);
        PumpPastActivationDelay(server, probe);

        bot.SendRipcordRequest(DeploySector);
        int seen = PumpUntil(server, () => bot.RipcordActivates.Count >= 1, 4000);
        Assert.True(seen < 4000, "no RIPCORD_ACTIVATE before destroying the probe");
        Assert.True(ServerShip(server, rid).IsRipcordActive);

        ResetRipcord(bot);
        ctx.Deploy.Remove(probe);   // destroyed/expired → removed from the sector (Probe.OnUpdate / ReceiveDamage)

        seen = PumpUntil(server, () => bot.RipcordAborteds.Count >= 1, 4000);
        Assert.True(seen < 4000, "no RIPCORD_ABORTED after the probe vanished");
        var after = ServerShip(server, rid);
        Assert.False(after.IsRipcordActive);
        Assert.Null(after.RipcordModel);
        Assert.NotEqual(DeploySector, after.Sector!.Id);   // never teleported to the probe's sector

        Console.WriteLine($"PROBE-RIPCORD-LIVE: ship {rid} ripcord to {DeploySector} aborted when probe {probe.Id} vanished mid-countdown -> RIPCORD_ABORTED, no teleport");
    }
}
