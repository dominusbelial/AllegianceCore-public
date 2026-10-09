using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P590 — pod rescue. Three parts:
///   (1) contact rescue WORKS for client-driven ships (a real ejected pod,
///       flown by its own client via native pose updates, rescues the instant
///       it reaches a friendly rescue-capable ship — the port's
///       <c>Sector.ResolveRescueEvents</c> + <c>Server.PerformRescue</c>);
///   (2) the c_cidPickup pickup order (a pod commanding a friendly
///       rescue-capable ship) arms an NPC diverter and streams a
///       "prepare for recovery" queued order to a real-player rescuer;
///   (3) the proactive pass assigns an idle rescue-capable AI diverter to a
///       flying pod (bots come to pods unprompted), at most one per pod, with
///       combat/task priority.
/// Native references: contact rescue <c>shipIGC.cpp:1027-1035</c>;
/// <c>RescueShipEvent</c> <c>FedSrv.CPP:13033-13074</c>; pickup order
/// <c>WinTrek.cpp:10666-10680</c> / <c>trekigc.cpp:4893-4897</c>;
/// legality <c>shipIGC.h:1661-1678</c>.
/// </summary>
[Collection("LiveNetwork")]
public class P590PodRescueTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // XXV_simple (measured, P111): 1052 = no-station fly sector.
    private const short FlySector = 1052;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
        Games = [new Gm { Name = "P590 Rescue", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer,
        Time = 596u, DwCookie = 0x596u, CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 5990000000000000000ul, DrmHash = "p590",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Boot a server and spawn the single POD peer (bot <paramref name="podName"/>,
    /// side <paramref name="podSide"/>, hull carrying <paramref name="podCaps"/>) in the fly
    /// sector at <paramref name="podPos"/>. Extra ships passed in are placed manually as
    /// NPC/side ships by the caller (server already booted here).</summary>
    private static (Srv server, FltBot podBot, Ship podShip) BootPodPeer(
        string podName, short podCaps, short podSide, Vector3 podPos)
    {
        var server = new Srv();
        Ship pod = null!;
        int n = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            var baseHull = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            pod = new Ship(id, baseHull with { HabmCapabilities = podCaps }, podPos, Vector3.Zero, Orientation.Identity) { SideId = podSide };
            mission.PlaceShip(pod, fly);
            n++;
            return (pod!, fly);
        };
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // P180 immediate-flyable

        var podBot = new FltBot();
        podBot.Connect(server.BoundPort, podName);
        podBot.SendLogonRequest(Request(podName));
        PumpUntil(server, () => podBot.ShipAssignment is not null, 4000);
        Assert.NotNull(pod);
        return (server, podBot, pod);
    }

    /// <summary>Manually place an extra ship in the fly sector (an NPC diverter,
    /// or a second peer ship for the order tests) and return it.</summary>
    private static Ship PlaceExtra(Srv server, Ship extra)
    {
        var mission = server.Missions.Single();
        mission.PlaceShip(extra, mission.Sectors.First(s => s.Id == FlySector));
        return extra;
    }

    /// <summary>Monotonic id allocator for extra (manually-placed, non-peer)
    /// ships. <c>server.Ships</c> only enumerates PEER ships, so deriving ids
    /// from it collides for multiple extras; a private counter above the peer-id
    /// range (peer ids here are ~8000-8100) gives each extra a unique id.</summary>
    private static int _extraId = 30400;
    private static short NextExtraId() => (short)_extraId++;

    private static Ship RescueCapable(Srv server, short side, Vector3 pos, bool isNpc)
    {
        var mission = server.Missions.Single();
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First(h => h.HasCapability(DataHullTypeIGC.HabmRescue));
        return new Ship(NextExtraId(), hull, pos, Vector3.Zero, Orientation.Identity)
        { SideId = side, IsNpc = isNpc };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (1) — contact rescue for CLIENT-DRIVEN ships, real eject path
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>⭐ A real ejected lifepod, flown by its OWN client (native pose
    /// updates, EJECT-rotated cookie), rescues the instant it reaches a friendly
    /// rescue-capable ship. This is the brief's headline scenario; without the
    /// Bot harness consuming the EJECT cookie (P540b) the pod's pose updates
    /// are dropped and it freezes at the eject point — no rescue (stale
    /// position). Failing-first on today's code before the harness fix.</summary>
    [Fact]
    public void Client_driven_ejected_pod_rescues_on_reaching_friendly()
    {
        // Custom boot: a real destroyable victim peer + a rescue-capable friend.
        var srv = new Srv();
        Ship victim = null!, rescuerShip = null!;
        int n = 0;
        srv.ShipSpawner = (core, mission, id) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            if (n == 0)
            {
                var bh = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord) && !h.HasCapability(DataHullTypeIGC.HabmLifepod));
                victim = new Ship(id, bh, new Vector3(610f, 655f, 880f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
                mission.PlaceShip(victim, fly);
            }
            else
            {
                var rh = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => h.HasCapability(DataHullTypeIGC.HabmRescue));
                // Rescuer parked just outside the ~35u contact radius of the
                // eject site (victim at 610,655,880), so the pod needs only a
                // short, reliable client-driven flight to reach it.
                rescuerShip = new Ship(id, rh, new Vector3(665f, 650f, 880f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
                mission.PlaceShip(rescuerShip, fly);
            }
            n++;
            return n == 1 ? (victim!, fly) : (rescuerShip!, fly);
        };
        srv.Boot(Config());
        foreach (var m in srv.Missions) m.Stage = MissionStage.Started;

        var vBot = new FltBot(); var rBot = new FltBot();
        vBot.Connect(srv.BoundPort, "VICT"); rBot.Connect(srv.BoundPort, "SCRU");
        vBot.SendLogonRequest(Request("VICT")); rBot.SendLogonRequest(Request("SCRU"));
        PumpUntil(srv, () => vBot.ShipAssignment is not null && rBot.ShipAssignment is not null, 4000);
        Assert.False(victim.HullType.HasCapability(DataHullTypeIGC.HabmLifepod));
        Assert.True(rescuerShip.HullType.HasCapability(DataHullTypeIGC.HabmRescue));

        // Real eject: lethal environmental damage.
        victim.ReceiveDamage(0, victim.HitPoints * 2f, srv.Missions[0].LastUpdate, victim.Position, Vector3.Zero, launcher: null);
        for (int i = 0; i < 5; i++) srv.TickSim();
        Assert.True(victim.HullType.HasCapability(DataHullTypeIGC.HabmLifepod), "eject did not produce a lifepod");
        Assert.False(victim.Dead, "ejected pod born dead (P308)");

        // Wait for the pod bot to actually RECEIVE the EJECT broadcast and adopt
        // the rotated pose cookie (P540b) — without it the pod's pose updates are
        // dropped (stale cookie) and it never flies. This is the flake guard.
        int cook = PumpUntil(srv, () => vBot.Ejects.Count >= 1, 4000);
        Assert.True(cook < 4000, "pod bot never received the EJECT message (rotated cookie)");

        // Drive the POD (client-driven, EJECT-rotated cookie) onto the rescuer.
        // First SETTLE: plant the pod at its current server position with zero
        // velocity so the eject burst's outward drift is cancelled and the
        // server's accepted position equals ours (kills the pose-rejection race),
        // then walk it to the rescuer in small sub-20m steps.
        byte cookie = vBot.PoseCookie;
        var start = victim.Position;
        vBot.SendOwnShipPose((uint)(srv.SimNow * 1000.0), cookie, start, Vector3.Zero, Orientation.Identity, ControlData.Neutral, 0, new float[3]);
        srv.TickSim(); srv.TickSim(); srv.TickSim();
        bool rescued = false;
        int steps = 40;
        for (int i = 0; i < steps && !rescued; i++)
        {
            var t = (i + 1) / (float)steps;
            var pos = new Vector3(
                start.X + (rescuerShip.Position.X - start.X) * t,
                start.Y + (rescuerShip.Position.Y - start.Y) * t,
                start.Z + (rescuerShip.Position.Z - start.Z) * t);
            vBot.SendOwnShipPose((uint)(srv.SimNow * 1000.0), cookie, pos, Vector3.Zero, Orientation.Identity, ControlData.Neutral, 0, new float[3]);
            srv.TickSim();
            srv.TickSim();
            if (rBot.PlayerRescueds.Count >= 1) rescued = true;
        }
        int seen = PumpUntil(srv, () => vBot.PlayerRescueds.Count >= 1 && rBot.PlayerRescueds.Count >= 1, 1000);
        Assert.True(seen < 1000, "NO PLAYER_RESCUED after driving the real ejected pod onto the friendly — client-driven contact rescue broken");

        var v = Assert.Single(vBot.PlayerRescueds);
        Assert.Equal(rescuerShip.Id, v.ShipIdRescuer);
        Assert.Equal(victim.Id, v.ShipIdRescuee);
        Assert.NotNull(victim.StationId);   // rescued pod docked at a friendly station

        Console.WriteLine($"P590-P1: client-driven ejected pod {victim.Id} flew into friendly {rescuerShip.Id} -> PLAYER_RESCUED rescuer={v.ShipIdRescuer}/rescuee={v.ShipIdRescuee}; pod docked at station {victim.StationId}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) — the c_cidPickup pickup order
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>⭐ A pod issues a c_cidPickup order to a same-side rescue-capable
    /// NPC: the NPC's pickup-diversion goal is armed, it flies to the pod, and
    /// the contact pass rescues it (PLAYER_RESCUED reaches the pod's peer).</summary>
    [Fact]
    public void Pod_pickup_order_arms_NPC_diverter_and_rescues()
    {
        var podPos = new Vector3(520f, 648f, 880f);
        var (server, podBot, podShip) = BootPodPeer("PODP", DataHullTypeIGC.HabmLifepod, 0, podPos);
        var npc = RescueCapable(server, 0, new Vector3(760f, 642f, 872f), isNpc: true);
        PlaceExtra(server, npc);

        Assert.True(podShip.HullType.HasCapability(DataHullTypeIGC.HabmLifepod));
        Assert.True(npc.HullType.HasCapability(DataHullTypeIGC.HabmRescue));
        Assert.Null(npc.PickupTarget);

        // Pod commands the rescue-capable NPC to pick it up (c_cidPickup).
        podBot.SendOrderChange(npc.Id, OrderChange.CidPickup, OrderChange.CmdQueued, IgcObjectType.Ship, podShip.Id);
        for (int i = 0; i < 5; i++) server.TickSim();

        // ⭐ The diverter is armed.
        Assert.NotNull(npc.PickupTarget);
        Assert.Equal(podShip.Id, npc.PickupTarget!.Id);
        Console.WriteLine($"P590-P2: pod {podShip.Id} ordered NPC {npc.Id} (c_cidPickup) -> PickupTarget armed");

        // The NPC autopilot flies to the pod; contact performs the rescue.
        int seen = PumpUntil(server, () => podBot.PlayerRescueds.Count >= 1, 4000);
        Assert.True(seen < 4000, "NPC diverter did not reach and rescue the pod");
        var r = Assert.Single(podBot.PlayerRescueds);
        Assert.Equal(npc.Id, r.ShipIdRescuer);
        Assert.Equal(podShip.Id, r.ShipIdRescuee);
        Assert.NotNull(podShip.StationId);

        Console.WriteLine($"P590-P2: NPC {npc.Id} diverted to pod {podShip.Id}, touched, rescued -> PLAYER_RESCUED rescuer={r.ShipIdRescuer}/rescuee={r.ShipIdRescuee}; pod docked@{podShip.StationId}");
    }

    /// <summary>⭐ A pod issues a c_cidPickup order to a REAL-PLAYER (peer)
    /// rescue-capable rescuer: the order is queued to the rescuer's client
    /// ("prepare for recovery" state, native trekigc.cpp:4896) and the rescuer
    /// is marked PickupTarget.</summary>
    [Fact]
    public void Pod_pickup_order_streams_queued_order_to_real_player_rescuer()
    {
        var server = new Srv();
        Ship pod = null!, rescuer = null!;
        int n = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            var baseHull = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            if (n == 0)
            {
                pod = new Ship(id, baseHull with { HabmCapabilities = DataHullTypeIGC.HabmLifepod }, new Vector3(500f, 644f, 880f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
                mission.PlaceShip(pod, fly);
            }
            else
            {
                rescuer = new Ship(id, baseHull with { HabmCapabilities = DataHullTypeIGC.HabmRescue }, new Vector3(560f, 647f, 884f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
                mission.PlaceShip(rescuer, fly);
            }
            n++;
            return n == 1 ? (pod!, fly) : (rescuer!, fly);
        };
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        var podBot = new FltBot(); var rescBot = new FltBot();
        podBot.Connect(server.BoundPort, "PDPX"); rescBot.Connect(server.BoundPort, "SCUX");
        podBot.SendLogonRequest(Request("PDPX")); rescBot.SendLogonRequest(Request("SCUX"));
        PumpUntil(server, () => podBot.ShipAssignment is not null && rescBot.ShipAssignment is not null, 4000);
        Assert.NotNull(pod); Assert.NotNull(rescuer);
        Assert.True(pod.HullType.HasCapability(DataHullTypeIGC.HabmLifepod));
        Assert.True(rescuer.HullType.HasCapability(DataHullTypeIGC.HabmRescue));

        rescBot.ResetLeadershipSignals();
        podBot.SendOrderChange(rescuer.Id, OrderChange.CidPickup, OrderChange.CmdQueued, IgcObjectType.Ship, pod.Id);
        int seen = PumpUntil(server, () => rescBot.QueuedCommandUpdates.Any(q => q.CommandId == OrderChange.CidPickup), 4000);
        Assert.True(seen < 4000, "rescuer peer never received the c_cidPickup queued order");

        var q = rescBot.QueuedCommandUpdates.First(x => x.CommandId == OrderChange.CidPickup);
        Assert.Equal(pod.Id, q.TargetId);       // the pod is the pickup subject
        Assert.Equal(IgcObjectType.Ship, q.TargetType);
        Assert.Equal(pod.Id, rescuer.PickupTarget!.Id);   // rescuer marked too (controller can act)

        Console.WriteLine($"P590-P2b: pod {pod.Id} -> c_cidPickup order queued to real-player rescuer {rescuer.Id}: QueuedCommandUpdate(CidPickup, Ship #{pod.Id}); rescuer.PickupTarget={rescuer.PickupTarget.Id}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) — proactive bot pickup (bots come to pods unprompted)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>⭐ An idle rescue-capable AI diverter within range of a flying pod
    /// is proactively assigned (no order needed) and rescues it; at most ONE
    /// diverter per pod (the nearer of two idle candidates wins; the other is
    /// never assigned).</summary>
    [Fact]
    public void Proactive_assignment_diverts_nearest_idle_diverter_and_rescues()
    {
        var podPos = new Vector3(520f, 649f, 880f);
        var (server, podBot, podShip) = BootPodPeer("PODQ", DataHullTypeIGC.HabmLifepod, 0, podPos);

        // Two idle rescue-capable NPC candidates: near and far (both well outside
        // the ~35u contact radius so the pod is NOT contact-rescued in the first
        // ticks — the assignment must survive until we assert it, then the diverter
        // flies the rest of the way).
        var nearIdle = RescueCapable(server, 0, new Vector3(900f, 621f, 614f), isNpc: true);   // ~450u from pod
        var farIdle = RescueCapable(server, 0, new Vector3(3400f, 581f, 561f), isNpc: true);   // ~2900u (still < PodPickupRange)
        PlaceExtra(server, nearIdle);
        PlaceExtra(server, farIdle);

        // A couple of ticks for the proactive pass to run.
        for (int i = 0; i < 5; i++) server.TickSim();

        // ⭐ At most one diverter per pod; the NEAREST idle one is chosen.
        var assigned = new[] { nearIdle, farIdle }.Count(s => s.PickupTarget is not null);
        Assert.Equal(1, assigned);
        Assert.Equal(podShip.Id, nearIdle.PickupTarget!.Id);
        Assert.Null(farIdle.PickupTarget);

        Console.WriteLine($"P590-P3: proactive pass assigned nearest idle diverter {nearIdle.Id} to pod {podShip.Id}; far {farIdle.Id} NOT assigned (at most one)");

        // The diverter rescues the pod (contact pass).
        int seen = PumpUntil(server, () => podBot.PlayerRescueds.Count >= 1, 4000);
        Assert.True(seen < 4000, "proactively-assigned diverter did not rescue the pod");
        var fr = podBot.PlayerRescueds.First();
        Assert.Equal(nearIdle.Id, fr.ShipIdRescuer);
        Assert.Equal(podShip.Id, fr.ShipIdRescuee);
        Assert.NotNull(podShip.StationId);   // rescued pod docked at a friendly station
        Console.WriteLine($"P590-P3: diverter {nearIdle.Id} rescued pod {podShip.Id} -> PLAYER_RESCUED rescuer={fr.ShipIdRescuer}/rescuee={fr.ShipIdRescuee}; pod docked@{podShip.StationId}");
    }

    /// <summary>⭐ Combat-priority rule: a rescue-capable AI diverter that is
    /// ENGAGED (has an AttackTarget) is never assigned by the proactive pass —
    /// even when it is the NEAREST candidate. The idle farther ship wins the
    /// assignment. (Assignment-only assertion; the engaged ship sits just
    /// outside the contact radius so its presence never forces a rescue.)
    /// This is the brief's "don't abandon a dogfight at close range" rule.</summary>
    [Fact]
    public void Engaged_diverter_is_not_proactively_assigned()
    {
        var podPos = new Vector3(520f, 649f, 880f);
        var (server, podBot, podShip) = BootPodPeer("PODE", DataHullTypeIGC.HabmLifepod, 0, podPos);

        // The NEAREST candidate is engaged; the farther one is idle.
        var nearEngaged = RescueCapable(server, 0, new Vector3(600f, 603f, 618f), isNpc: true);  // nearest
        var nearIdle = RescueCapable(server, 0, new Vector3(760f, 593f, 608f), isNpc: true);     // farther, idle
        PlaceExtra(server, nearEngaged);
        PlaceExtra(server, nearIdle);

        // nearEngaged is in a dogfight (AttackTarget), enemy parked far away.
        var mission = server.Missions.Single();
        var enemyHull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
        var enemy = new Ship(NextExtraId(), enemyHull with { HabmCapabilities = 0 }, new Vector3(2800f, 462f, 464f), Vector3.Zero, Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(enemy, mission.Sectors.First(s => s.Id == FlySector));
        nearEngaged.AttackTarget = enemy;

        for (int i = 0; i < 5; i++) server.TickSim();

        // ⭐ The engaged NEAREST ship is NOT assigned (combat priority); the
        // idle farther ship is.
        Assert.Null(nearEngaged.PickupTarget);
        Assert.Equal(podShip.Id, nearIdle.PickupTarget!.Id);
        Assert.NotNull(nearEngaged.AttackTarget);   // still in the fight
        Console.WriteLine($"P590-P3b: engaged nearest diverter {nearEngaged.Id} NOT assigned (combat priority); idle farther {nearIdle.Id} assigned");
    }
}
