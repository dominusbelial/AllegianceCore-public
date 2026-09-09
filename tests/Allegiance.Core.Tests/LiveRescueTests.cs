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
/// P115 — the real <c>PLAYER_RESCUED</c>(16) trigger, the port of the native
/// rescue branch of <c>CshipIGC::HandleCollision</c> (<c>shipIGC.cpp:1027-1035</c>)
/// and <c>FedSrvSiteBase::RescueShipEvent</c> (<c>FedSrv.CPP:13033-13074</c>).
/// A friendly same-side <c>c_habmLifepod</c> ship that drifts within proximity
/// of a <c>c_habmRescue</c>-capable ship is rescued: <c>PLAYER_RESCUED</c>(16)
/// to both ships' connections, then the rescued ship is placed docked at the
/// nearest friendly station. Triggered by the per-tick proximity pass
/// <c>Sector.ResolveRescueEvents</c> (the port's point-in-sphere stand-in for
/// ship-vs-ship collision, mirroring <c>ResolveDockCollisions</c>); performed by
/// <c>Server.PerformRescue</c>.
///
/// Topology (measured, P111): the real side-0 garrison is in sector 1053 and
/// side-1's in 1061; sector 1052 (Borasisi) has no station. Both test ships are
/// placed flying in 1052 (so they stay flying and never proximity-dock), and
/// the rescue's station search places the rescued pod at the real friendly
/// side-0 station in 1053.
///
/// Scenarios (real loopback Bot vs in-process Server):
///   (1) same-side lifepod + rescue-capable ship in range -> PLAYER_RESCUED(16)
///       with the correct rescuer/rescuee ids to BOTH bots; pod docked at a real
///       friendly station;
///   (2) opposite-side pair -> no rescue (no false positive);
///   (3) wrong capability bits (partner lacks c_habmRescue) -> no rescue;
///   (4) out-of-range pair (both capable, same side, too far) -> no rescue;
///   (5) direction does not matter -> swapped spawn order still rescues with the
///       correct roles (rescuer-to-pod vs pod-to-rescuer are indistinguishable).
///
/// The codec round-trips byte-exact in Wire.ObjectSpottedPlayerRescuedWireTests.
/// </summary>
[Collection("LiveNetwork")]
public class LiveRescueTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    // XXV_simple (measured, P111 report): 1052 = no-station fly sector, 1053 =
    // side-0 garrison sector, 1061 = side-1 garrison sector.
    private const short FlySector = 1052;
    private const short Side0StationSector = 1053;
    private const short Side1StationSector = 1061;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Rescue Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 661u, DwCookie = 0x661u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 7788000000000000000ul, DrmHash = "resc",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Ship ServerShip(Srv server, short id) =>
        server.Ships.First(s => s.Ship.Id == id).Ship;

    private sealed class RescueSpawn
    {
        public Ship Pod = null!, Rescuer = null!;
    }

    /// <summary>Two-ship spawn injection. The first spawned ship carries
    /// <paramref name="podCaps"/> (the "lifepod" role), the second
    /// <paramref name="rescuerCaps"/> (the "rescuer" role); sides and positions
    /// are caller-supplied so one injector covers every scenario (same/opposite
    /// side, in/out of range, swapped roles). Hulls are the lowest-id
    /// non-<c>c_habmNoRipcord</c> hull cloned with exactly the given capability
    /// mask, so a test pins the exact bits a rescue does/doesn't require.</summary>
    private static RescueSpawn InjectRescueSpawn(
        Srv server,
        short podCaps, short rescuerCaps,
        short podSide, short rescuerSide,
        Vector3 podPos, Vector3 rescuerPos)
    {
        var sp = new RescueSpawn();
        int n = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            var baseHull = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            if (n == 0)
            {
                sp.Pod = new Ship(shipId, baseHull with { HabmCapabilities = podCaps }, podPos, Vector3.Zero, Orientation.Identity)
                { SideId = podSide };
                mission.PlaceShip(sp.Pod, fly);
            }
            else
            {
                sp.Rescuer = new Ship(shipId, baseHull with { HabmCapabilities = rescuerCaps }, rescuerPos, Vector3.Zero, Orientation.Identity)
                { SideId = rescuerSide };
                mission.PlaceShip(sp.Rescuer, fly);
            }
            n++;
            return n == 1 ? (sp.Pod!, fly) : (sp.Rescuer!, fly);
        };
        return sp;
    }

    /// <summary>Boot a two-bot server with the injected spawn. The first bot to
    /// log on owns the lifepod-role ship, the second the rescuer-role ship; both
    /// are placed flying (never docked) in <see cref="FlySector"/>.</summary>
    private static (Srv server, FltBot podBot, FltBot rescBot, RescueSpawn sp)
        Setup(short podCaps, short rescuerCaps, short podSide, short rescuerSide, Vector3 podPos, Vector3 rescuerPos)
    {
        var server = new Srv();
        var sp = InjectRescueSpawn(server, podCaps, rescuerCaps, podSide, rescuerSide, podPos, rescuerPos);
        server.Boot(Config());

        var a = new FltBot(); var b = new FltBot();
        a.Connect(server.BoundPort, "PODR"); b.Connect(server.BoundPort, "SCUR");
        a.SendLogonRequest(Request("PODR")); b.SendLogonRequest(Request("SCUR"));
        PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);

        Assert.NotNull(sp.Pod);
        Assert.NotNull(sp.Rescuer);
        // Both placed flying (never docked) in the no-station fly sector.
        Assert.Null(sp.Pod.StationId);
        Assert.Null(sp.Rescuer.StationId);
        Assert.Equal(FlySector, sp.Pod.Sector!.Id);
        Assert.Equal(FlySector, sp.Rescuer.Sector!.Id);
        return (server, a, b, sp);
    }

    /// <summary>⭐ Scenario 1 — a same-side lifepod within range of a same-side
    /// rescue-capable ship broadcasts PLAYER_RESCUED(16) to BOTH ships'
    /// connections with the correct rescuer/rescuee ids, and the rescued pod
    /// ends up docked at a real friendly station (the side-0 garrison in 1053).</summary>
    [Fact]
    public void Lifepod_in_range_of_rescue_ship_is_rescued_and_docked()
    {
        // Spawn both ships side 0 but FAR apart so no rescue fires during the
        // join pump (Setup asserts both still flying). Then drift the lifepod
        // onto the rescue ship and verify the rescue.
        var (server, podBot, rescBot, sp) = Setup(
            DataHullTypeIGC.HabmLifepod, DataHullTypeIGC.HabmRescue,
            0, 0, new Vector3(300f, 620f, 880f), new Vector3(700f, 620f, 860f));

        // The lifepod drifts within range of the rescue ship.
        sp.Pod.Position = sp.Rescuer.Position;
        server.TickSim();

        int seen = PumpUntil(server,
            () => podBot.PlayerRescueds.Count >= 1 && rescBot.PlayerRescueds.Count >= 1, 4000);
        Assert.True(seen < 4000, "no PLAYER_RESCUED(16) reached both bots");

        // Both bots received the SAME frame with the correct ids: rescuer is the
        // rescue-capable ship, rescuee is the lifepod.
        var p = Assert.Single(podBot.PlayerRescueds);
        var r = Assert.Single(rescBot.PlayerRescueds);
        Assert.Equal(sp.Rescuer.Id, p.ShipIdRescuer);
        Assert.Equal(sp.Pod.Id, p.ShipIdRescuee);
        Assert.Equal(p.ShipIdRescuer, r.ShipIdRescuer);
        Assert.Equal(p.ShipIdRescuee, r.ShipIdRescuee);

        // The rescued pod ends up DOCKED at a real friendly station.
        var pod = ServerShip(server, sp.Pod.Id);
        Assert.NotNull(pod.StationId);
        var mission = server.Missions.Single();
        var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.StationId == pod.StationId!.Value);
        Assert.Equal((short)0, station.SideId);             // friendly
        Assert.Equal(Side0StationSector, pod.Sector!.Id);   // moved into the station's cluster

        Console.WriteLine($"RESCUE-LIVE: ship {sp.Pod.Id} (lifepod) rescued by ship {sp.Rescuer.Id} -> PLAYER_RESCUED(16) rescuer={p.ShipIdRescuer}/rescuee={p.ShipIdRescuee} to BOTH bots; pod docked at station {station.StationId} in sector {pod.Sector.Id}");
    }

    /// <summary>⭐ Scenario 2 — an opposite-side pair is never rescued: no
    /// <c>PLAYER_RESCUED</c> reaches either bot and the pod stays flying (no
    /// false positive across many ticks).</summary>
    [Fact]
    public void Opposite_side_pair_is_not_rescued()
    {
        // Lifepod on side 0, rescue-capable ship on side 1, parked on top of
        // each other (would trivially pass the proximity check) — the friendly
        // same-side gate must still reject them.
        var (server, podBot, rescBot, sp) = Setup(
            DataHullTypeIGC.HabmLifepod, DataHullTypeIGC.HabmRescue,
            0, 1, new Vector3(300f, 620f, 880f), new Vector3(300f, 620f, 880f));

        for (int i = 0; i < 60; i++) server.TickSim();

        Assert.Empty(podBot.PlayerRescueds);
        Assert.Empty(rescBot.PlayerRescueds);
        Assert.Null(ServerShip(server, sp.Pod.Id).StationId);       // never docked

        Console.WriteLine($"RESCUE-NEG-SIDE: ship {sp.Pod.Id} (side 0 lifepod) + ship {sp.Rescuer.Id} (side 1 rescuer) on top of each other -> NO PLAYER_RESCUED, pod stays flying (same-side gate)");
    }

    /// <summary>⭐ Scenario 3 — wrong capability bits: the partner lacks
    /// <c>c_habmRescue</c> (a lifepod next to a non-rescue ship), so no rescue
    /// fires even in perfect proximity on the same side.</summary>
    [Fact]
    public void Partner_without_rescue_capability_is_not_rescued()
    {
        // Pod has c_habmLifepod; the "rescuer" ship has NEITHER bit -> not a
        // rescue-capable partner.
        var (server, podBot, rescBot, sp) = Setup(
            DataHullTypeIGC.HabmLifepod, 0,
            0, 0, new Vector3(300f, 620f, 880f), new Vector3(300f, 620f, 880f));

        for (int i = 0; i < 60; i++) server.TickSim();

        Assert.Empty(podBot.PlayerRescueds);
        Assert.Empty(rescBot.PlayerRescueds);
        Assert.Null(ServerShip(server, sp.Pod.Id).StationId);

        Console.WriteLine($"RESCUE-NEG-CAPS: ship {sp.Pod.Id} (lifepod) + ship {sp.Rescuer.Id} (no rescue bit) on top of each other same-side -> NO PLAYER_RESCUED, pod stays flying");
    }

    /// <summary>⭐ Scenario 4 — an out-of-range pair (both capable, same side,
    /// far apart) is not rescued: proximity is a hard gate.</summary>
    [Fact]
    public void Out_of_range_pair_is_not_rescued()
    {
        // Same side, both capable, but separated far beyond any plausible
        // sum-of-radii (1 km; ship radii are ~0.5×tens of units).
        var (server, podBot, rescBot, sp) = Setup(
            DataHullTypeIGC.HabmLifepod, DataHullTypeIGC.HabmRescue,
            0, 0, new Vector3(300f, 620f, 880f), new Vector3(1300f, 620f, 880f));

        // Sanity: they really are far apart (threshold = sum of 0.5×Length).
        float radPod = 0.5f * sp.Pod.HullType.Length;
        float radResc = 0.5f * sp.Rescuer.HullType.Length;
        float sep = (sp.Pod.Position - sp.Rescuer.Position).Length();
        Assert.True(sep > radPod + radResc, $"test separation {sep} not beyond {radPod + radResc}");

        for (int i = 0; i < 60; i++) server.TickSim();

        Assert.Empty(podBot.PlayerRescueds);
        Assert.Empty(rescBot.PlayerRescueds);
        Assert.Null(ServerShip(server, sp.Pod.Id).StationId);

        Console.WriteLine($"RESCUE-NEG-RANGE: ship {sp.Pod.Id} + ship {sp.Rescuer.Id} same-side capable but {sep:F0}u apart (> {radPod + radResc:F0} threshold) -> NO PLAYER_RESCUED, pod stays flying");
    }

    /// <summary>⭐ Scenario 5 — direction does not matter. Swapping the spawn
    /// order (rescuer is the first/bot-A ship, lifepod the second/bot-B ship)
    /// must still rescue with the CORRECT roles — the pass derives roles from
    /// capability bits, not from which ship is first in the sector.</summary>
    [Fact]
    public void Swapped_spawn_order_still_rescues_with_correct_roles()
    {
        // Reverse the standard order: the RESCUE-CAPABLE ship is spawned FIRST
        // (so it is sp.Pod by spawn-order naming) and the LIFEPOD second
        // (sp.Rescuer). The pass must still derive the roles from the
        // capability bits — the rescue-capable ship is the rescuer, the lifepod
        // the rescuee — regardless of which is first in the sector.
        var (server, podBot, rescBot, sp) = Setup(
            DataHullTypeIGC.HabmRescue, DataHullTypeIGC.HabmLifepod,
            0, 0, new Vector3(300f, 620f, 870f), new Vector3(720f, 650f, 890f));
        var rescuer = sp.Pod;      // sp.Pod has the c_habmRescue caps (spawned first)
        var lifepod = sp.Rescuer;  // sp.Rescuer has the c_habmLifepod caps (spawned second)

        // The RESCUER drifts onto the lifepod this time (direction reversed).
        rescuer.Position = lifepod.Position;
        server.TickSim();

        int seen = PumpUntil(server,
            () => podBot.PlayerRescueds.Count >= 1 && rescBot.PlayerRescueds.Count >= 1, 4000);
        Assert.True(seen < 4000, "no PLAYER_RESCUED reached both bots in swapped order");

        var p = Assert.Single(podBot.PlayerRescueds);
        var r = Assert.Single(rescBot.PlayerRescueds);
        // Roles still correct by capability: rescuer == the rescue-capable ship,
        // rescuee == the lifepod.
        Assert.Equal(rescuer.Id, p.ShipIdRescuer);
        Assert.Equal(lifepod.Id, p.ShipIdRescuee);
        Assert.Equal(p.ShipIdRescuer, r.ShipIdRescuer);
        Assert.Equal(p.ShipIdRescuee, r.ShipIdRescuee);
        Assert.NotNull(ServerShip(server, lifepod.Id).StationId);   // lifepod docked

        Console.WriteLine($"RESCUE-SYM: swapped order still rescues ship {lifepod.Id} (lifepod) by ship {rescuer.Id} -> PLAYER_RESCUED rescuer={p.ShipIdRescuer}/rescuee={p.ShipIdRescuee}, pod docked");
    }
}
