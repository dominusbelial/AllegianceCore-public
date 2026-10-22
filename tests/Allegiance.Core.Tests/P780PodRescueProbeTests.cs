using System.Reflection;
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
/// P780 — the rescue-PROBE pod-rescue pass (<c>Sector.ResolveProbeRescueEvents</c>
/// → <c>DrainStationRescues</c> with a <c>null</c> Station →
/// <c>Server.PerformStationRescue</c>'s probe branch), the port of native's
/// OT_probe collision branch of <c>CshipIGC::HandleCollision</c>
/// (<c>shipIGC.cpp:897-909</c>): a lifepod that touches a probe whose type
/// carries <c>c_eabmRescue</c> (0x2000 — own/allied pods only) or
/// <c>c_eabmRescueAny</c> (0x4000 — any pod) is rescued via
/// <c>RescueShipEvent(pod, NULL)</c> — no wire, no credit, dock at the pod's
/// OWN side's restart station (<c>FedSrv.CPP:13033-13076</c>). The probe is
/// not consumed and rescues from the moment it is created (no activation
/// delay — <c>m_time0</c> only gates ripcord use, probeigc.h:248-254).
///
/// Real PCore001 rescue-probe inventory (verified in
/// <see cref="Rescue_probe_inventory_matches_the_core"/>): expendable 19
/// "Rescue" (IC, 0x2000, radius 6, life 1200, hp 25, price 250), 51 "Beacon"
/// (Technoflux, 0x2000, hp 1), 2 "Sanctuary" (Belters, 0x4000, life 1800).
/// </summary>
public class P780PodRescueProbeTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Mission Assembly(IIgcSite? site = null)
    {
        var core = Core();
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));
        var sides = new[]
        {
            new SideSpec(0, "Alpha", 1),
            new SideSpec(1, "Bravo", 3),
        };
        var m = Mission.Assemble(core, map, sides, site ?? new RecordingIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>A REAL PCore001 probe of the given expendable type placed in the
    /// sector. <paramref name="overrideEabm"/> re-writes the type's
    /// <c>EabmCapabilities</c> when a test needs a specific bit (a record
    /// <c>with</c> clone, the same technique StationRescueModelTests uses for
    /// station capability bits).</summary>
    private static Probe AddProbe(Mission m, short sectorId, short probeTypeId, short side, Vector3 pos,
        short? overrideEabm = null, short id = 6700)
    {
        var type = m.Core.ProbeTypes[probeTypeId];
        if (overrideEabm is { } eabm) type = type with { EabmCapabilities = eabm };
        var sector = m.Sectors.First(s => s.Id == sectorId);
        var probe = new Probe(id, sector, type, pos, now: 1000.0, sideId: side);
        sector.Add(probe);
        return probe;
    }

    /// <summary>A real civ-1 lifepod (dreghpod, hull 33, Length=4) placed flying
    /// in the sector.</summary>
    private static Ship Lifepod(Mission m, short sectorId, short id, Vector3 pos, short side, Vector3? velocity = null)
    {
        var hull = m.Core.HullTypes[m.Core.Civilizations[1].Lifepod];
        var s = new Ship(id, hull, pos, velocity ?? Vector3.Zero, Orientation.Identity) { SideId = side };
        m.PlaceShip(s, sectorId);
        return s;
    }

    /// <summary>A non-lifepod hull (lowest-id non-c_habmNoRipcord hull, the
    /// same choice LiveRescueTests makes) for the over-matching guard.</summary>
    private static Ship Fighter(Mission m, short sectorId, short id, Vector3 pos, short side)
    {
        var hull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
        var s = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = side };
        m.PlaceShip(s, sectorId);
        return s;
    }

    private const short FlySector = 1052;   // no real station in XXV_simple (LiveRescueTests topology)

    /// <summary>Drive a few ticks so the rescue pass runs.</summary>
    private static void Tick(Sector sector, int n = 5)
    {
        double now = 1000.0;
        for (int i = 0; i < n; i++)
            sector.Update(now += 0.25);
    }

    /// <summary>Verify Claude's rescue-probe inventory dump against the real
    /// core: exactly three probe types carry a rescue bit, with the recorded
    /// eabm/radius/lifespan/HP/price and civ-gating base techs (bit 1 common;
    /// IC pairs it with bit 18, Technoflux with bit 20, Belters with bit 102 —
    /// read from the launcher's TtbmRequired at byte offsets 1 / 3 / 51).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rescue_probe_inventory_matches_the_core()
    {
        Require.Artwork();
        var core = Core();
        var rescue = core.ProbeTypes.Values.Where(p => (p.EabmCapabilities & 0x6000) != 0).ToList();
        Assert.Equal(3, rescue.Count);

        var id19 = core.ProbeTypes[(short)19];
        Assert.Equal(0x2000, id19.EabmCapabilities);
        Assert.Equal("Rescue", id19.LauncherName);
        Assert.Equal(6f, id19.Radius);
        Assert.Equal(1200f, id19.Lifespan);
        Assert.Equal(25f, id19.HitPoints);
        Assert.Equal(250, id19.Price);
        Assert.Equal(2, id19.LauncherTtbmRequired[0]);    // base tech bit 1
        Assert.Equal(4, id19.LauncherTtbmRequired[2]);    // tech bit 18 (Iron Coalition)

        var id51 = core.ProbeTypes[(short)51];
        Assert.Equal(0x2000, id51.EabmCapabilities);
        Assert.Equal("Beacon", id51.LauncherName);
        Assert.Equal(6f, id51.Radius);
        Assert.Equal(1200f, id51.Lifespan);
        Assert.Equal(1f, id51.HitPoints);
        Assert.Equal(100, id51.Price);
        Assert.Equal(2, id51.LauncherTtbmRequired[0]);    // base tech bit 1
        Assert.Equal(16, id51.LauncherTtbmRequired[2]);   // tech bit 20 (Technoflux)

        var id2 = core.ProbeTypes[(short)2];
        Assert.Equal(0x4000, id2.EabmCapabilities);
        Assert.Equal("Sanctuary", id2.LauncherName);
        Assert.Equal(6f, id2.Radius);
        Assert.Equal(1800f, id2.Lifespan);
        Assert.Equal(25f, id2.HitPoints);
        Assert.Equal(100, id2.Price);
        Assert.Equal(2, id2.LauncherTtbmRequired[0]);     // base tech bit 1
        Assert.Equal(64, id2.LauncherTtbmRequired[12]);   // tech bit 102 (Belters)

        // The inventory table's lifepod hull facts: length 4–5, speed 50–60,
        // plus the Observer (length 2, speed 160) also carrying c_habmLifepod.
        var pods = core.HullTypes.Values.Where(h => h.HasCapability(DataHullTypeIGC.HabmLifepod)).ToList();
        Assert.Contains(pods, h => h.HullId == 245 && h.Name == "Observer" && h.Length == 2f && h.Speed == 160f);
        Assert.All(pods.Where(h => h.HullId != 245), h => { Assert.True(h.Length is >= 4f and <= 5f); Assert.True(h.Speed is >= 50f and <= 60f); });
        Console.WriteLine($"P780-INVENTORY: rescue probes 19/Rescue(0x2000)/51/Beacon(0x2000)/2/Sanctuary(0x4000) + {pods.Count} lifepod hulls all match Claude's dump");
    }

    /// <summary>⭐ Test 2a — an ENEMY pod touching a c_eabmRescue probe is NOT
    /// rescued (native requires the pod's side to be the probe's own or an
    /// ally, shipIGC.cpp:905). Passes on both old and new code (guards
    /// over-matching).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Enemy_pod_touching_c_eabmRescue_probe_is_not_rescued()
    {
        Require.Artwork();
        var m = Assembly();
        var probe = AddProbe(m, FlySector, 19, side: 0, new Vector3(0, 0, 0));
        var pod = Lifepod(m, FlySector, 1, new Vector3(3, 0, 0), side: 1);   // sides 0/1 not allied
        bool allied = m.Sides.Any(s => s.Id == 0 && s.AllyGroup is { } g && m.Sides.Any(t => t.Id == 1 && t.AllyGroup == g));
        Assert.False(allied);

        var sector = m.Sectors.First(s => s.Id == FlySector);
        Tick(sector);

        Assert.Empty(sector.DrainStationRescues());
        Assert.Null(pod.StationId);
        Assert.Contains(probe, sector.Models);
        Console.WriteLine($"P780-ENEMY: side-1 pod {pod.Id} on side-0 Rescue probe {probe.Id} (eabm 0x2000, sides not allied) -> nothing queued, pod flying");
    }

    /// <summary>⭐ Test 2b — an ALLIED pod (same real Side.AllyGroup alliance,
    /// IsAlliedSide) touching a c_eabmRescue probe IS queued. Fails on the
    /// unfixed code (no probe pass at all).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Allied_pod_touching_c_eabmRescue_probe_is_queued()
    {
        Require.Artwork();
        var m = Assembly();
        // Real alliance: both sides share a real Side.AllyGroup group (the
        // SideSpecs carry none, so both start null — IsAlliedSide is false).
        m.Sides.First(s => s.Id == 0).AllyGroup = 7;
        m.Sides.First(s => s.Id == 1).AllyGroup = 7;
        var probe = AddProbe(m, FlySector, 19, side: 0, new Vector3(0, 0, 0));
        var pod = Lifepod(m, FlySector, 1, new Vector3(3, 0, 0), side: 1);

        var sector = m.Sectors.First(s => s.Id == FlySector);
        Tick(sector);

        var det = sector.DrainStationRescues();
        Assert.NotEmpty(det);                                   // FAILS on the base: no probe pass
        Assert.All(det, d => Assert.True(ReferenceEquals(d.Rescuee, pod)));
        Assert.All(det, d => Assert.Null(d.Station));           // NULL-rescuer probe rescue marker
        Console.WriteLine($"P780-ALLIED: side-1 (allied) pod {pod.Id} on side-0 Rescue probe {probe.Id} -> queued NULL-rescuer rescue on {det.Count} ticks");
    }

    /// <summary>⭐ Test 3 — a side-1 pod touching a side-0 c_eabmRescueAny
    /// probe ("Sanctuary") IS rescued, and the destination is the pod's OWN
    /// side (native FindTarget + GetBase(own side) fallback,
    /// FedSrv.CPP:13063-13071). Model level: the queue entry is the rescue; the
    /// own-side destination is pinned with the Server's restart search.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Any_pod_touching_c_eabmRescueAny_probe_is_rescued_to_its_own_side()
    {
        Require.Artwork();
        var m = Assembly();
        var probe = AddProbe(m, FlySector, 2, side: 0, new Vector3(0, 0, 0));
        var podPos = new Vector3(3, 0, 0);
        var pod = Lifepod(m, FlySector, 1, podPos, side: 1);

        var sector = m.Sectors.First(s => s.Id == FlySector);
        Tick(sector);

        var det = sector.DrainStationRescues();
        Assert.NotEmpty(det);                                   // FAILS on the base
        Assert.All(det, d => Assert.True(ReferenceEquals(d.Rescuee, pod)));
        Assert.All(det, d => Assert.Null(d.Station));

        // The destination the host will pick is a SIDE-1 (own side) station —
        // native GetBase(own side), not the probe's side (0).
        var dest = global::Allegiance.Server.Server.FindNearestRestartStation(m, 1, FlySector, podPos);
        Assert.NotNull(dest);
        Assert.Equal((short)1, dest!.Value.SideId);
        Assert.True(m.Core.StationTypes[dest.Value.StationTypeId].HasCapability(DataStationTypeIGC.SabmRestart));
        Console.WriteLine($"P780-ANY: side-1 pod {pod.Id} on side-0 Sanctuary probe {probe.Id} (0x4000) -> queued; destination = own-side station {dest.Value.StationId} (side {dest.Value.SideId})");
    }

    /// <summary>⭐ Test 8 — a site that refuses (returns false from
    /// RescueShipEvent, as native's FedSrvSiteBase does when no destination
    /// station exists) leaves the pod flying: nothing queued. The meaningful
    /// assertion on the unfixed code is that the site WAS CALLED with
    /// (pod, null) — the pass asks before queueing, as the other passes do.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Site_refusal_leaves_pod_flying()
    {
        var site = new RefusingSite();
        var m = Assembly(site);
        var probe = AddProbe(m, FlySector, 19, side: 0, new Vector3(0, 0, 0));
        var pod = Lifepod(m, FlySector, 1, new Vector3(3, 0, 0), side: 0);

        var sector = m.Sectors.First(s => s.Id == FlySector);
        Tick(sector);

        Assert.True(site.Calls >= 1, "RescueShipEvent was never called — the pass did not run");   // FAILS on the base
        Assert.All(site.Seen, c => Assert.True(ReferenceEquals(c.rescuee, pod) && c.rescuer is null));
        Assert.Empty(sector.DrainStationRescues());
        Assert.Null(pod.StationId);
        Console.WriteLine($"P780-REFUSAL: site refused {site.Calls} RescueShipEvent(pod, null) call(s) -> nothing queued, pod {pod.Id} stays flying");
    }

    /// <summary>⭐ Test 7 — guards over-matching: a probe with NO rescue bit
    /// (a scanner, eabm 0) and a NON-lifepod hull on a real type-19 probe are
    /// both ignored. Passes on both old and new code.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Non_rescue_probe_or_non_lifepod_is_ignored()
    {
        Require.Artwork();
        var m = Assembly();
        var scanner = AddProbe(m, FlySector, 19, side: 0, new Vector3(0, 0, 0), overrideEabm: 0, id: 6701);
        var rescue = AddProbe(m, FlySector, 19, side: 0, new Vector3(100, 0, 0), id: 6702);
        var pod = Lifepod(m, FlySector, 1, new Vector3(3, 0, 0), side: 0);        // on the scanner
        var fighter = Fighter(m, FlySector, 2, new Vector3(103, 0, 0), side: 0);  // on the rescue probe

        var sector = m.Sectors.First(s => s.Id == FlySector);
        Tick(sector);

        Assert.Empty(sector.DrainStationRescues());
        Assert.Null(pod.StationId);
        Assert.Null(fighter.StationId);
        Console.WriteLine($"P780-NEG: pod {pod.Id} on eabm-0 scanner {scanner.Id} and fighter {fighter.Id} on Rescue probe {rescue.Id} -> nothing queued (no over-match)");
    }

    /// <summary>⭐ Test 6 — a freshly created probe (no pump before contact)
    /// rescues pod A, then pod B on a later tick, and is NOT consumed: still in
    /// <c>sector.Models</c> with the same hit points (Fraction 1). Fails on the
    /// unfixed code (no rescue at all).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_is_not_consumed_and_rescues_from_creation()
    {
        Require.Artwork();
        var m = Assembly();
        var sector = m.Sectors.First(s => s.Id == FlySector);
        // Created and touched on the same tick — no pump between Add and Update.
        var probe = AddProbe(m, FlySector, 19, side: 0, new Vector3(0, 0, 0));
        var podA = Lifepod(m, FlySector, 1, new Vector3(2, 0, 0), side: 0);

        double now = 1000.0;
        sector.Update(now += 0.25);                 // first contact — rescues from creation
        var detA = sector.DrainStationRescues();
        Assert.Contains(detA, d => ReferenceEquals(d.Rescuee, podA) && d.Station is null);   // FAILS on the base

        var podB = Lifepod(m, FlySector, 2, new Vector3(2, 0, 0), side: 0);
        sector.Update(now += 0.25);                 // a later tick — rescues again
        var detB = sector.DrainStationRescues();
        Assert.Contains(detB, d => ReferenceEquals(d.Rescuee, podB) && d.Station is null);

        // Not consumed: still present, hit points untouched.
        Assert.Contains(probe, sector.Models);
        Assert.Equal(1f, probe.Fraction);
        Console.WriteLine($"P780-REUSE: fresh probe {probe.Id} rescued pod {podA.Id} on its creation tick and pod {podB.Id} on the next; probe still live at Fraction {probe.Fraction}");
    }

    /// <summary>⭐ Test 9 — the rescue-probe counter: 0 → 1 on deploy, → 0 on
    /// destruction, → 0 on expiry; a scanner probe never counts. New API
    /// (<c>Sector.RescueProbeCount</c> test seam).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rescue_probe_counter_tracks_add_expire_destroy()
    {
        Require.Artwork();
        var m = Assembly();
        var sector = m.Sectors.First(s => s.Id == FlySector);
        Assert.Equal(0, sector.RescueProbeCount);

        var scanner = AddProbe(m, FlySector, 19, side: 0, new Vector3(0, 0, 0), overrideEabm: 0, id: 6703);
        Assert.Equal(0, sector.RescueProbeCount);          // a scanner probe stays 0

        var probe = AddProbe(m, FlySector, 19, side: 0, new Vector3(50, 0, 0), id: 6704);
        Assert.Equal(1, sector.RescueProbeCount);          // 0 → 1 on deploy

        // Destroy (direct-hit path): count returns to 0.
        probe.ReceiveDamage(type: 1, amount: 1e6f, now: 1000.0, position1: probe.Position, position2: probe.Position, launcher: null);
        Assert.True(probe.Dead);
        Assert.Equal(0, sector.RescueProbeCount);
        Assert.DoesNotContain(probe, sector.Models);

        // Expire: another deploy → 1, then tick past its expiration → 0.
        var probe2 = AddProbe(m, FlySector, 51, side: 0, new Vector3(-50, 0, 0), id: 6705);
        Assert.Equal(1, sector.RescueProbeCount);
        probe2.SetExpiration(1000.0);
        Tick(sector, 2);                                    // now > 1000 → OnUpdate expiry branch
        Assert.Equal(0, sector.RescueProbeCount);
        Assert.DoesNotContain(probe2, sector.Models);
        Console.WriteLine("P780-COUNTER: 0 -> 1 on deploy -> 0 on destroy -> 1 -> 0 on expire; scanner probe never counts");
    }

    /// <summary>⭐ Test 9b — the pass is allocation-free: run
    /// <c>ProbeRescuePass</c> in isolation (other passes allocate, so the whole
    /// tick is not assertable) over a 200-ship sector with no rescue probe and
    /// with 2 rescue probes; the GC delta must not grow with the number of
    /// calls once warmed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_rescue_pass_allocates_nothing()
    {
        Require.Artwork();
        var m = Assembly();
        var sector = m.Sectors.First(s => s.Id == FlySector);

        // 200 flying ships, 20 of them lifepods, none near a probe.
        for (int i = 0; i < 200; i++)
        {
            short side = (short)(i % 2);
            var pos = new Vector3(2000 + (i % 20) * 7f, 2000 + i * 1.5f, 2000);
            if (i < 20) Lifepod(m, FlySector, (short)(100 + i), pos, side);
            else Fighter(m, FlySector, (short)(100 + i), pos, side);
        }

        // No rescue probe: the gather still walks every ship (the seam bypasses
        // the counter early-out). Warm generously — tiered JIT settles the
        // method's allocation behaviour after a few dozen calls — then measure.
        Assert.Equal(0, sector.RescueProbeCount);
        for (int i = 0; i < 300; i++) sector.ProbeRescuePass(0.05f);        // warm
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) sector.ProbeRescuePass(0.05f);
        long d0 = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, d0);                                                // no growth with N

        // Two rescue probes (far from every ship): the gather+scan runs, still free.
        AddProbe(m, FlySector, 19, side: 0, new Vector3(-3000, 0, 0), id: 6706);
        AddProbe(m, FlySector, 2, side: 0, new Vector3(3000, 0, 0), id: 6707);
        Assert.Equal(2, sector.RescueProbeCount);
        for (int i = 0; i < 300; i++) sector.ProbeRescuePass(0.05f);        // warm
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) sector.ProbeRescuePass(0.05f);
        long d2 = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, d2);                                                // no growth with N
        Console.WriteLine($"P780-ALLOC: ProbeRescuePass over 200 ships: 0 probes -> {d0} B, 2 probes -> {d2} B per 100 calls (allocation-free)");
    }

    /// <summary>An IIgcSite that REFUSES rescues (native's
    /// <c>return pstation ? ... : false</c> with no destination station,
    /// FedSrv.CPP:13074), recording every call for the site-gate assertion.
    /// RecordingIgcSite is sealed, so this stands alone on the interface
    /// defaults.</summary>
    private sealed class RefusingSite : IIgcSite
    {
        public int Calls;
        public readonly List<(Ship rescuee, Ship? rescuer)> Seen = [];
        public bool RescueShipEvent(IShipIgc shipRescued, IShipIgc? shipRescuer)
        {
            Calls++;
            Seen.Add(((Ship)shipRescued, (Ship?)shipRescuer));
            return false;
        }
    }
}

/// <summary>
/// P780 — the LIVE half: a real loopback probe deploy (real
/// <c>FIRE_EXPENDABLE</c> through a mounted dispenser, type 19 "Rescue") and a
/// real ejected-pod hull drifting into it on a real <c>Server</c>. The pod is
/// docked at the station <c>FindFriendlyStation</c> resolves (the nearest
/// own-side <c>c_sabmRestart</c> station), with NO <c>PLAYER_RESCUED</c> and no
/// rescue credit (native NULL rescuer, FedSrv.CPP:13036-13058), and the dock
/// cancels the P279 oxygen timer.
/// </summary>
[Collection("LiveNetwork")]
public class P780PodRescueProbeLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private const short FlySector = 1052;            // no station (XXV_simple)
    private const short Side0StationSector = 1053;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P780 ProbeRescue Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 680u, DwCookie = 0x680u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 7788000000000001000ul, DrmHash = "p780",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Ship ServerShip(Srv server, short id) =>
        server.Ships.First(s => s.Ship.Id == id).Ship;

    private sealed class Ctx
    {
        public Ship Pod = null!, Deployer = null!;
        public FltBot PodBot = null!, DeployerBot = null!;
    }

    /// <summary>Boot a two-bot side-0 server: the FIRST logon gets the REAL
    /// civ-1 lifepod hull (dreghpod) parked in the fly sector, the SECOND a
    /// normal hull parked ~30u away (the probe deployer).</summary>
    private static (Srv server, Ctx ctx) Setup(double? podEnduranceOverride = null)
    {
        var server = new Srv();
        var ctx = new Ctx();
        int n = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            if (n == 0)
            {
                var hull = core.HullTypes[core.Civilizations[1].Lifepod];   // real dreghpod lifepod
                ctx.Pod = new Ship(shipId, hull, new Vector3(0f, 620f, 880f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
                if (podEnduranceOverride is { } eo) ctx.Pod.LifepodEnduranceOverrideSeconds = eo;
                mission.PlaceShip(ctx.Pod, fly);
            }
            else
            {
                var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
                ctx.Deployer = new Ship(shipId, hull, new Vector3(30f, 620f, 880f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
                mission.PlaceShip(ctx.Deployer, fly);
                ctx.Deployer.Controls = new ControlData(0f, 0f, 0f, -1f);   // kill-throttle: hold position
            }
            n++;
            return n == 1 ? (ctx.Pod!, fly) : (ctx.Deployer!, fly);
        };
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby

        ctx.PodBot = new FltBot(); ctx.DeployerBot = new FltBot();
        ctx.PodBot.Connect(server.BoundPort, "P780POD");
        ctx.DeployerBot.Connect(server.BoundPort, "P780DEP");
        ctx.PodBot.SendLogonRequest(Request("P780POD"));
        ctx.DeployerBot.SendLogonRequest(Request("P780DEP"));
        PumpUntil(server, () => ctx.PodBot.ShipAssignment is not null && ctx.DeployerBot.ShipAssignment is not null, 4000);
        Assert.NotNull(ctx.Pod);
        Assert.NotNull(ctx.Deployer);
        Assert.Null(ctx.Pod.StationId);
        return (server, ctx);
    }

    /// <summary>Deploy the REAL type-19 "Rescue" probe through the REAL
    /// FIRE_EXPENDABLE path (mounted probe launcher + the wire request) at the
    /// deployer's position, mirroring LiveTeleportProbeRipcordTests.DeployViaWire.</summary>
    private static Probe DeployRescueProbe(Srv server, Ctx ctx)
    {
        // Under load the two logons can complete out of order relative to the
        // spawn bookkeeping, so resolve the bots by the ship ids they actually
        // own (a fire request is routed by the sender's own ship).
        var deployerBot = ctx.DeployerBot.ShipAssignment!.Ship.ShipId == ctx.Deployer.Id ? ctx.DeployerBot : ctx.PodBot;
        var core = server.Missions.Single().Core;
        var type = core.ProbeTypes[(short)19];
        Assert.Equal(0x2000, type.EabmCapabilities);
        var part = new DataPartTypeIGC(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "rescue", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: 778, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.ProbeLauncher(type.ExpendableTypeId));
        ctx.Deployer.TryRemovePart(EquipmentType.Dispenser, 0, out _);
        ctx.Deployer.MountPart(part, amount: 3, mountId: 0, fractionMounted: 255);
        // Under heavy machine load a FIRE_EXPENDABLE request can be dropped
        // before the server's receive thread registers the mounted launcher —
        // re-send on each pump pass until the probe appears (3 rounds in the
        // dispenser, so at most 3 fires). The observed-failure rate without
        // this retry was 2 in ~30 runs; with it, 0.
        var deploy = server.Missions.Single().Sectors.First(s => s.Id == FlySector);
        for (int attempt = 0; attempt < 3 && !deploy.Models.OfType<Probe>().Any(p => p.ProbeType.ExpendableTypeId == 19); attempt++)
        {
            deployerBot.SendFireExpendable(EquipmentType.Dispenser);
            PumpUntil(server,
                () => deploy.Models.OfType<Probe>().Any(p => p.ProbeType.ExpendableTypeId == 19), 4000);
        }
        int seen = PumpUntil(server,
            () => deploy.Models.OfType<Probe>().Any(p => p.ProbeType.ExpendableTypeId == 19), 2000);
        Assert.True(seen < 2000, "no type-19 Rescue probe deployed via FIRE_EXPENDABLE");
        return deploy.Models.OfType<Probe>().Single(p => p.ProbeType.ExpendableTypeId == 19);
    }

    /// <summary>⭐ Test 1 — a side-0 pod drifting into a side-0 type-19
    /// "Rescue" probe (deployed via the real FIRE_EXPENDABLE path) ends up
    /// docked at the station <c>FindFriendlyStation</c> resolves: the nearest
    /// own-side <c>c_sabmRestart</c> station, in that station's sector.
    /// Fails on the unfixed code (the pod stays adrift).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Own_side_pod_touching_rescue_probe_is_docked_at_nearest_friendly_restart_station()
    {
        Require.Artwork();
        var (server, ctx) = Setup();
        var probe = DeployRescueProbe(server, ctx);

        // The pod drifts at the probe (its own ejection autopilot's job on the
        // client; here a sustained 20 u/s steer toward the deployed probe —
        // re-set every tick, because an unpiloted pod's velocity decays by drag).
        int seen = 0;
        while (ctx.Pod.StationId is null && seen < 2000)
        {
            ctx.Pod.Velocity = (probe.Position - ctx.Pod.Position).Normalize() * 20f;
            server.TickSim(); seen++;
        }
        Assert.True(seen < 2000, $"pod was never rescued (probe at {probe.Position}, pod at {ctx.Pod.Position})");

        // The docked station is the one FindFriendlyStation resolves — the
        // nearest own-side restart-capable station (sector 1052 has none, so
        // the search's GetBase leg is position-independent).
        var mission = server.Missions.Single();
        var expected = Srv.FindNearestRestartStation(mission, 0, FlySector, ctx.Pod.Position);
        Assert.NotNull(expected);
        Assert.True(mission.Core.StationTypes[expected!.Value.StationTypeId].HasCapability(DataStationTypeIGC.SabmRestart),
            "docked station type must carry c_sabmRestart");
        Assert.Equal(expected.Value.StationId, ctx.Pod.StationId);
        Assert.Equal((short)0, expected.Value.SideId);
        var podSector = mission.Sectors.First(s => s.Stations.Any(st => st.StationId == ctx.Pod.StationId!.Value));
        Assert.Equal(podSector.Id, ctx.Pod.Sector!.Id);   // the pod is IN that station's sector

        Console.WriteLine($"P780-LIVE: pod {ctx.Pod.Id} drifted into FIRE_EXPENDABLE-deployed Rescue probe {probe.Id} -> docked at station {ctx.Pod.StationId} (side 0, c_sabmRestart) in sector {podSector.Id} after {seen} ticks");
    }

    /// <summary>⭐ Test 4 — a probe rescue sends NO PLAYER_RESCUED(16) to the
    /// pod's peer and earns NO rescue credit for anyone (the deployer's
    /// PlayerScore stays at 0 rescues): the whole wire/award block sits inside
    /// native's <c>if (pshipRescuer)</c> (FedSrv.CPP:13036-13058). Fails on the
    /// unfixed code on the "docked" precondition, so the dock is asserted
    /// first.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_rescue_sends_no_PLAYER_RESCUED_and_no_rescue_credit()
    {
        Require.Artwork();
        var (server, ctx) = Setup();
        var probe = DeployRescueProbe(server, ctx);

        int seen = 0;
        while (ctx.Pod.StationId is null && seen < 2000)
        {
            ctx.Pod.Velocity = (probe.Position - ctx.Pod.Position).Normalize() * 20f;
            server.TickSim(); seen++;
        }
        Assert.True(seen < 2000, "pod was never rescued (docked precondition)");   // FAILS on the base

        // No message 16 to the pod's peer, and nobody's score moved —
        // including the probe deployer's.
        Assert.Empty(ctx.PodBot.PlayerRescueds);
        Assert.Empty(ctx.DeployerBot.PlayerRescueds);
        var deployerScore = server.Ships.First(l => ReferenceEquals(l.Ship, ctx.Deployer)).PlayerScore;
        var podScore = server.Ships.First(l => ReferenceEquals(l.Ship, ctx.Pod)).PlayerScore;
        Assert.Equal(0, deployerScore.Rescues);
        Assert.Equal(0, podScore.Rescues);
        Console.WriteLine($"P780-NOWIRE: probe rescue of pod {ctx.Pod.Id} docked it with no PLAYER_RESCUED(16) to either peer; deployer rescues = {deployerScore.Rescues}, pod rescues = {podScore.Rescues}");
    }

    /// <summary>⭐ Test 5 — the probe rescue cancels the P279 oxygen timer: a
    /// pod with a 10 s endurance override (the P279 seam) touches the probe
    /// within ~1 s of its spawn, and long past the deadline it is ALIVE and
    /// docked with no <c>[oxygen]</c> kill in the server log. Fails on the
    /// unfixed code (the pod dies of oxygen).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_rescue_cancels_oxygen_timer()
    {
        Require.Artwork();
        var (server, ctx) = Setup(podEnduranceOverride: 10.0);
        var probe = DeployRescueProbe(server, ctx);

        // Touch within ~1 s of the spawn (20 ticks at 20 ups), well inside the
        // 10 s window (the deploy pump already consumed a few of them).
        for (int i = 0; i < 5; i++) server.TickSim();

        // Capture the server log so the absence of an [oxygen] kill is assertable.
        var log = new StringWriter();
        var oldOut = Console.Out;
        Console.SetOut(log);
        try
        {
            int seen = 0;
            while (ctx.Pod.StationId is null && seen < 2000)
            {
                ctx.Pod.Velocity = (probe.Position - ctx.Pod.Position).Normalize() * 40f;
                server.TickSim(); seen++;
            }
            Assert.True(seen < 2000, "pod was never rescued");                      // FAILS on the base
            // Pump well past the 10 s oxygen deadline (200+ ticks past the
            // dock) — the docked pod's oxygen timer is cancelled, it survives.
            for (int i = 0; i < 300; i++) server.TickSim();
        }
        finally { Console.SetOut(oldOut); }

        Assert.False(ctx.Pod.Dead, "pod died (oxygen kill?)");
        Assert.NotNull(ctx.Pod.StationId);
        Assert.DoesNotContain("[oxygen]", log.ToString());
        Console.WriteLine($"P780-OXYGEN: pod {ctx.Pod.Id} (10 s endurance) rescued within ~1 s, survived 300 ticks past the deadline docked at {ctx.Pod.StationId}; log had no [oxygen] kill");
    }

    /// <summary>⭐ Test 10 — the full real-deploy proof: a real
    /// FIRE_EXPENDABLE type-19 deploy by a side-0 bot, and a SECOND side-0
    /// logon in the real civ lifepod hull starting ~30 u away drifting at the
    /// probe, docks within the tick budget and receives no PLAYER_RESCUED.
    /// Fails on the unfixed code.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rescue_probe_deployed_via_FIRE_EXPENDABLE_rescues_a_pod()
    {
        Require.Artwork();
        var (server, ctx) = Setup();
        var probe = DeployRescueProbe(server, ctx);
        Assert.Equal(FlySector, ctx.Pod.Sector!.Id);

        // ~30u separation at spawn; the pod drifts at the probe (ejection
        // autopilot's steer, replicated as a plain velocity).
        float separation = (ctx.Pod.Position - probe.Position).Length();
        Assert.True(separation is > 10f and < 60f, $"unexpected spawn separation {separation}");
        // Contact within ~2 s of sustained 20 u/s drift + dock: 2000 ticks is a
        // generous budget at 20 ups.
        int seen = 0;
        while (ctx.Pod.StationId is null && seen < 2000)
        {
            ctx.Pod.Velocity = (probe.Position - ctx.Pod.Position).Normalize() * 20f;
            server.TickSim(); seen++;
        }
        Assert.True(seen < 2000, "pod was never rescued");                          // FAILS on the base
        var podBot = ctx.PodBot.ShipAssignment!.Ship.ShipId == ctx.Pod.Id ? ctx.PodBot : ctx.DeployerBot;
        Assert.Empty(podBot.PlayerRescueds);                                        // and no message 16
        Assert.Empty(ctx.PodBot.PlayerRescueds);
        Assert.Empty(ctx.DeployerBot.PlayerRescueds);

        var mission = server.Missions.Single();
        var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.StationId == ctx.Pod.StationId!.Value);
        Assert.Equal((short)0, station.SideId);
        Console.WriteLine($"P780-DEPLOY: FIRE_EXPENDABLE Rescue probe {probe.Id} rescued drifting pod {ctx.Pod.Id} ({separation:F0}u spawn separation) -> docked at side-0 station {station.StationId} in sector {ctx.Pod.Sector!.Id} after {seen} ticks, no PLAYER_RESCUED");
    }
}
