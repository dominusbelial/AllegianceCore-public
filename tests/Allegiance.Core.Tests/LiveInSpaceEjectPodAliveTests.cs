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
/// P308 — an ejected lifepod must be born ALIVE, not dead.
///
/// The defect (found by a read-only glm-5.3 review of <c>Server.cs</c>):
/// <c>Ship.ReceiveDamage</c> zeroes <c>_fraction</c> BEFORE raising
/// <c>KillShipEvent</c> (<c>Ship.cs:3131-3132</c>), and <c>Ship.ChangeHull</c>
/// /native <c>SetBaseHullType</c> is documented NOT to reset it — so
/// <c>Server.EjectSurvivor</c>'s pod was born with <c>Dead == true</c> and
/// stayed dead forever, silently disabling the rescue pass
/// (<c>Sector.ResolveRescueEvents</c> requires <c>!s.Dead</c>, Sector.cs:772),
/// the heavy-update list (<c>GetShipUpdateCandidates</c> skips dead ships,
/// Sector.cs:410), and the combat/mine/explosion passes (all skip dead ships):
/// an invisible, invulnerable, undestroyable zombie pod. The fix restores the
/// pod to full hull right after the hull swap, at the eject site ONLY — all
/// downstream gates are left untouched.
///
/// The live proof: destroy a real peer-owned, non-lifepod ship that has a
/// rescue-capable teammate nearby, then assert
///   (1) the resulting Lifepod hull is NOT dead (<c>Dead == false</c>,
///       <c>Fraction == 1.0f</c>) the instant it is born;
///   (2) the pod appears in the sector's <c>GetShipUpdateCandidates()</c>
///       (the heavy-update gate that used to skip dead ships);
///   (3) the same-side rescue-capable teammate can now actually trigger
///       <c>ResolveRescueEvents</c> against it — a real <c>PLAYER_RESCUED</c>(16)
///       fires and the pod is placed docked at a friendly station, where before
///       the fix nothing happened.
///
/// Must-fail-if-removed: reverting the <c>SetFraction(1.0f)</c> in
/// <c>EjectSurvivor</c> makes the pod born <c>Dead</c> — assertion (1) fails,
/// the pod drops out of (2), and being excluded from the rescue candidate list
/// it never satisfies (3).
/// </summary>
[Collection("LiveNetwork")]
public class LiveInSpaceEjectPodAliveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Eject Pod Alive", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 882u, DwCookie = 0x882u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8510000000000000000ul, DrmHash = "poda",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>
    /// Boot a two-bot server. Both peers ride side 0 in a no-station fly
    /// sector: the victim (spawn 0, a non-lifepod/non-rescue clone of the
    /// lowest flight hull) and the rescuer (spawn 1, the same base hull cloned
    /// with exactly the <c>c_habmRescue</c> bit). Positions are caller-supplied.
    /// </summary>
    private static (Srv server, FltBot victimBot, FltBot rescuerBot, Ship victimShip, Ship rescuerShip)
        Setup(Vector3 victimPos, Vector3 rescuerPos)
    {
        var server = new Srv();
        var noStationSector = (Sector?)null;
        var n = 0;
        Ship victim = null!, rescuer = null!;
        server.ShipSpawner = (core, mission, id) =>
        {
            noStationSector ??= mission.Sectors.First(s => s.Stations.Count == 0);
            var baseHull = core.HullTypes.Values
                .OrderBy(h => h.HullId)
                .First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            if (n == 0)
            {
                // A clean fighter: no lifepod, no rescue — a normal destroyable ship.
                victim = new Ship(id, baseHull with { HabmCapabilities = 0 },
                    victimPos, Vector3.Zero, Orientation.Identity) { SideId = 0 };
                mission.PlaceShip(victim, noStationSector);
            }
            else
            {
                // The same hull carrying exactly the rescue capability bit.
                rescuer = new Ship(id, baseHull with { HabmCapabilities = DataHullTypeIGC.HabmRescue },
                    rescuerPos, Vector3.Zero, Orientation.Identity) { SideId = 0 };
                mission.PlaceShip(rescuer, noStationSector);
            }
            n++;
            return n == 1 ? (victim!, noStationSector) : (rescuer!, noStationSector);
        };
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started; // P180: immediate-flyable

        var a = new FltBot(); var b = new FltBot();
        a.Connect(server.BoundPort, "VICTIM"); b.Connect(server.BoundPort, "SCUER");
        a.SendLogonRequest(Request("VICTIM")); b.SendLogonRequest(Request("SCUER"));
        PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);

        Assert.NotNull(victim);
        Assert.NotNull(rescuer);
        return (server, a, b, victim, rescuer);
    }

    /// <summary>⭐ Destroy a real non-lifepod peer ship that has a same-side
    /// rescue-capable teammate: the ejected pod is born ALIVE (<c>Dead ==
    /// false</c>, full hull), shows up in <c>GetShipUpdateCandidates()</c>, and
    /// its teammate can now really trigger <c>ResolveRescueEvents</c> →
    /// <c>PLAYER_RESCUED</c>(16) + the pod is docked at a friendly station.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Ejected_pod_is_born_alive_and_can_be_rescued()
    {
        Require.Artwork();
        // Victim and rescuer start adjacent (same side 0) in the no-station
        // fly sector, so no rescue can pre-fire (no lifepod exists yet) and the
        // pod is born right beside its rescuer.
        var (server, victimBot, rescuerBot, victimShip, rescuerShip) =
            Setup(new Vector3(560f, 605f, 848f), new Vector3(580f, 609f, 852f));

        Assert.Equal((short)0, victimShip.SideId);
        Assert.Equal((short)0, rescuerShip.SideId);
        Assert.False(victimShip.HullType.HasCapability(DataHullTypeIGC.HabmLifepod), "precondition: victim is not already a lifepod");
        Assert.True(rescuerShip.HullType.HasCapability(DataHullTypeIGC.HabmRescue), "precondition: rescuer is rescue-capable");
        Assert.False(victimShip.HullType.HasCapability(DataHullTypeIGC.HabmRescue), "precondition: victim has no rescue bit");

        // Pre-eject: both flying, not docked, no rescue fired yet.
        Assert.Null(victimShip.StationId);
        Assert.Null(rescuerShip.StationId);
        Assert.Empty(victimBot.PlayerRescueds);
        Assert.Empty(rescuerBot.PlayerRescueds);

        var mission = server.Missions[0];
        var side0 = mission.Sides.First(s => s.Id == 0);
        short lifepodId = side0.Civilization.Lifepod;
        var lifepodHull = mission.Core.HullTypes[lifepodId];
        Assert.True(lifepodHull.HasCapability(DataHullTypeIGC.HabmLifepod), "core lifepod hull must carry c_habmLifepod");
        Assert.True(mission.Sectors.SelectMany(s => s.Stations).Any(st => st.SideId == 0), "side 0 owns a station (rescue placement target)");

        // Lethal damage that kills the victim and triggers the eject. Since P549
        // the same-side rescuer can no longer be the launcher — the native
        // friendly-fire gate (shipIGC.cpp:1222-1228) drops ALL positive damage
        // from a same-side launcher, so a same-side shot never lands. Use a null
        // (environmental) launcher instead: launcher side is irrelevant to
        // ejection (per the original test note), and this still walks the full
        // death → eject seam. Eject is synchronous inside the kill seam.
        Vector3 p1 = victimShip.Position;
        victimShip.ReceiveDamage(0, victimShip.HitPoints * 2f, mission.LastUpdate, p1, Vector3.Zero, launcher: null);

        // Give the eject's EJECT(77) broadcast a couple of ticks to flush, then
        // inspect the survivor (it is synchronous, so it is already swapped now).
        for (int i = 0; i < 5; i++) server.TickSim();

        // ⭐ (1) THE P308 CORE ASSERTION: the pod is born ALIVE, at full hull.
        Assert.Equal(lifepodId, victimShip.HullType.HullId);   // hull swapped to the side's Lifepod
        Assert.False(victimShip.Dead, "ejected lifepod must NOT be born dead");
        Assert.Equal(1.0f, victimShip.Fraction, 4);
        Assert.NotNull(victimShip.Sector);                     // still drifting (not removed, not respawned)

        // ⭐ (2) the heavy-update gate now admits the freshly-ejected pod.
        Assert.Contains(victimShip, victimShip.Sector!.GetShipUpdateCandidates());

        // Drift the pod onto the rescuer (the eject gives it outward velocity,
        // so pin the position like LiveRescueTests does), then let the rescue
        // proximity pass see the pair.
        victimShip.Position = rescuerShip.Position;
        server.TickSim();

        // ⭐ (3) a same-side rescue-capable ship can now REALLY rescue the pod.
        int seen = PumpUntil(server,
            () => victimBot.PlayerRescueds.Count >= 1 && rescuerBot.PlayerRescueds.Count >= 1, 4000);
        Assert.True(seen < 4000, "no PLAYER_RESCUED(16) reached both bots — the ejected pod was not admitted to the rescue pass");

        var v = Assert.Single(victimBot.PlayerRescueds);
        var r = Assert.Single(rescuerBot.PlayerRescueds);
        Assert.Equal(rescuerShip.Id, v.ShipIdRescuer);
        Assert.Equal(victimShip.Id, v.ShipIdRescuee);
        Assert.Equal(v.ShipIdRescuer, r.ShipIdRescuer);
        Assert.Equal(v.ShipIdRescuee, r.ShipIdRescuee);

        // The rescued pod is placed docked at a real friendly (side 0) station.
        Assert.NotNull(victimShip.StationId);
        Assert.Equal((short)0, mission.Sectors.SelectMany(s => s.Stations).First(st => st.StationId == victimShip.StationId!.Value).SideId);

        Console.WriteLine($"P308-LIVE: peer ship {victimShip.Id} destroyed by teammate {rescuerShip.Id} -> ejected as Lifepod {lifepodId}; born ALIVE (Dead={victimShip.Dead}, Fraction={victimShip.Fraction}); in GetShipUpdateCandidates; then PLAYER_RESCUED rescuer={v.ShipIdRescuer}/rescuee={v.ShipIdRescuee} to BOTH bots; pod docked at station {victimShip.StationId}.");
    }
}
