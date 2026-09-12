using Allegiance.Core;
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
/// P166 — the Wingman "defend an ally" decision layer (Stage 2), the port of
/// native's <c>c_cidDefend</c> re-evaluation (<c>Igc/shipIGC.cpp:2373-2407</c>)
/// plus its companion charge-centered acquisition scan
/// (<c>shipIGC.cpp:2680-2705</c>). A Wingman assigned to protect a charge
/// (<see cref="Ship.DefendTarget"/> set) engages enemies NEAR THE CHARGE, not
/// the nearest to itself, and — while chasing — re-evaluates each tick whether
/// to keep the current <see cref="Ship.AttackTarget"/>, switch to a closer
/// threat near the charge, or fall back to escorting (steer back toward the
/// charge) when the chased enemy drifts too far (distToProtectee &gt; 3500).
/// These live loopback tests prove the two decision outcomes, not just a flag:
/// <list type="bullet">
/// <item><b>Fallback to escort</b> — the defender engages an enemy near the
/// charge, the enemy flees beyond 3500 of the charge, and the Wingman actually
/// disengages (<see cref="Ship.AttackTarget"/> cleared) and flies back toward
/// the charge (measured distance decrease) — native's shouldReturn →
/// <c>SetCommand(c_cmdPlan, charge, c_cidDefend)</c>.</item>
/// <item><b>Switch to a closer threat</b> — the defender is chasing a distant
/// enemy (in the 2200-3500 band) when a closer enemy appears within 2500 of the
/// charge and inside the "1000 closer than the chased one" margin; it switches
/// <see cref="Ship.AttackTarget"/> to the closer threat — native's
/// <c>SetCommand(c_cmdPlan, pTarget, c_cidAttack)</c>.</item>
/// </list>
/// Harness mirrors P162 (booted in-process server + one seated bot, no Windows
/// VM); the peer is parked in a separate sector so the Wingman's sector contains
/// exactly the ships the test places (clean enemy control). DefendTarget is set
/// directly, the established pattern every prior decision-cycle test uses.
/// </summary>
[Collection("LiveNetwork")]
public sealed class NpcWingmanDefendLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P166 Wingman Defend Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 558u, DwCookie = 0x558u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 55300000000000006ul, DrmHash = "p166wingman",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Shared boot. <b>The peer is parked in the LAST sector</b> so the
    /// Wingman's sector (sector 0) contains only the ships the test places —
    /// clean, unambiguous enemy control for the charge-centered searches (the
    /// P162 negative-control Boot shape). Returns (server, mission, sector,
    /// npcSide, peerSide, wingmanDroneTypeId).</summary>
    private static (Srv Server, Mission Mission, Sector Sector, short NpcSide, short PeerSide, short WingmanDroneId)
        Boot()
    {
        var server = new Srv();
        Mission? mission = null;
        Sector? sector = null;
        short npcSide = 0;
        short peerSide = 0;
        short wingDroneId = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            var core = m.Core;
            peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;   // the OTHER side — no players

            var wing = core.DroneTypes.First(kv => kv.Value.PilotType == Ship.PilotTypeWingman);
            wingDroneId = wing.Key;

            // The seated peer player ship — parked in the LAST sector so it never
            // interferes with the Wingman's own sector's enemy searches.
            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var peerSector = m.Sectors[m.Sectors.Count - 1];
            var ship = new Ship(id, hull, new Vector3(990f, 626f, 628f), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(ship, peerSector);
            return (ship, peerSector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        // P178: keep the bot SEATED (no `using` → not disposed at Boot return)
        // so the mission stays Started — the server's win-by-elimination now
        // (correctly) ends a mission when its last player leaves. The server
        // closes this socket at teardown.
        var bot = new FltBot();
        bot.Connect(server.BoundPort, "p166_wingman");
        bot.SendLogonRequest(Request("p166_wingman"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);

        Assert.NotNull(mission); Assert.NotNull(sector);
        Assert.True(wingDroneId != 0, "core must expose a Wingman pilot-type drone");
        return (server, mission!, sector!, npcSide, peerSide, wingDroneId);
    }

    private static float Dist(Vector3 a, Vector3 b) => (b - a).Length();

    /// <summary>⭐ Fallback-to-escort leg: a Wingman assigned to defend a charge
    /// engages an enemy NEAR the charge, the enemy flees beyond 3500 of the
    /// charge, and the Wingman actually disengages and flies back toward the
    /// charge — native's shouldReturn → return-to-defend (shipIGC.cpp:2373-2407),
    /// evidenced by <see cref="Ship.AttackTarget"/> clearing and a measured
    /// distance decrease back to the charge.</summary>
    [Fact]
    public void Defender_disengages_when_chased_enemy_flees_and_returns_to_charge()
    {
        var (srv, mission, sector, npcSide, peerSide, wingDroneId) = Boot();
        using (srv)
        {
            var core = mission.Core;
            var hull = core.HullTypes.Values.OrderByDescending(h => h.Length).First();

            // ── The charge: an ally (npcSide, same side as the Wingman).
            Vector3 cp = new(600f, 610f, 605f);
            var charge = new Ship((short)6101, hull, cp, Vector3.Zero, Orientation.Identity) { SideId = npcSide };
            mission.PlaceShip(charge, sector);

            // ── The enemy: initially NEAR the charge — 838u away (well inside the
            //    1500 charge-centered acquire radius, and << 2200 so no re-eval),
            //    so the defending Wingman engages it.
            Vector3 e1p = new(1438f, 613f, 597f);
            var e1 = new Ship((short)6102, hull, e1p, Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            mission.PlaceShip(e1, sector);
            float e1Near = Dist(cp, e1p);
            Assert.InRange(e1Near, 650f, 930f);

            // ── The Wingman: parked very near the charge (~58u) and ASSIGNED to
            //    defend it (DefendTarget set directly, the established test
            //    pattern).
            Vector3 wp = new(658f, 599f, 611f);
            var wingman = srv.SpawnNpcShip(mission, sector, npcSide, wp, wingDroneId);
            Assert.Equal(Ship.PilotTypeWingman, wingman.PilotType);
            wingman.DefendTarget = charge;

            // Phase 1 — engage: the defending Wingman acquires the enemy NEAR THE
            // CHARGE via the Part A charge-centered scan (1500 radius), not some
            // nearest-to-itself pick.
            Pump(srv, () => wingman.AttackTarget is not null, 400);
            Assert.Same(e1, wingman.AttackTarget);
            Assert.Same(charge, wingman.DefendTarget);               // still protecting
            Console.WriteLine($"P166-ENGAGE: defender {wingman.Id} engaging {e1.Id} ({e1Near:F0}u from charge {charge.Id})");

            // Let it actually pursue e1 (fly away from the charge) so the later
            // "return" leg has real distance to close.
            Pump(srv, () => Dist(wingman.Position, cp) > 200f, 800);
            float chasedAway = Dist(wingman.Position, cp);
            Assert.True(chasedAway > 120f, $"defender should have pursued the enemy away from the charge: {chasedAway:F1}");
            Console.WriteLine($"P166-PURSUE: defender chased e1 to {chasedAway:F0}u from charge");

            // Phase 2 — flee: teleport e1 far beyond 3500 of the charge so the
            // native abandon rule (distToProtectee > 3500, shipIGC.cpp:2380) MUST
            // trigger a return-to-escort.
            Vector3 flee = new(10100f, 602f, 614f);
            e1.Position = flee;
            e1.Velocity = Vector3.Zero;
            float e1Far = Dist(cp, flee);
            Assert.True(e1Far > 4250f, $"e1 must flee well beyond 3500: {e1Far:F0}");

            Pump(srv, () => wingman.AttackTarget is null, 400);
            Assert.Null(wingman.AttackTarget);
            Console.WriteLine($"P166-DISENGAGE: defender gave up e1 (now {e1Far:F0}u from charge) → AttackTarget null");

            // Phase 3 — return to escort: the Wingman steers BACK toward the
            // charge (ApplyNpcAutopilot's DefendTarget branch, no AttackTarget).
            float d0 = Dist(wingman.Position, cp);
            Pump(srv, () => Dist(wingman.Position, cp) < d0 - 40f, 1500);
            float d1 = Dist(wingman.Position, cp);
            Assert.True(d1 < d0 - 40f,
                $"defender must fly back toward the charge: d0={d0:F1} → d1={d1:F1}");
            Assert.Same(charge, wingman.DefendTarget);
            Assert.Null(wingman.AttackTarget);                       // not re-engaging the fled foe
            Console.WriteLine($"P166-RETURN: defender flew back toward charge {charge.Id}: {d0:F1} → {d1:F1}");
        }
    }

    /// <summary>⭐ Switch-to-closer-threat leg: the defender is chasing a distant
    /// enemy (distToProtectee in the 2200-3500 band) when a CLOSER enemy appears
    /// within 2500 of the charge and inside the "1000 closer" margin; it switches
    /// <see cref="Ship.AttackTarget"/> to the closer threat — native's
    /// <c>SetCommand(c_cmdPlan, pTarget, c_cidAttack)</c> (shipIGC.cpp:2383-2389),
    /// the re-evaluation's other branch.</summary>
    [Fact]
    public void Defender_switches_to_closer_threat_near_charge_while_chasing_distant_one()
    {
        var (srv, mission, sector, npcSide, peerSide, wingDroneId) = Boot();
        using (srv)
        {
            var core = mission.Core;
            var hull = core.HullTypes.Values.OrderByDescending(h => h.Length).First();

            // ── The charge (ally).
            Vector3 cp = new(600f, 620f, 615f);
            var charge = new Ship((short)6201, hull, cp, Vector3.Zero, Orientation.Identity) { SideId = npcSide };
            mission.PlaceShip(charge, sector);

            // ── The DISTANT enemy the Wingman is already chasing: 2800u from the
            //    charge (in the 2200-3500 re-evaluation band, and OUTSIDE the
            //    2500 charge-centered scan radius).
            Vector3 e1p = new(3400f, 618f, 600f);
            var e1 = new Ship((short)6202, hull, e1p, Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            mission.PlaceShip(e1, sector);
            float e1Dist = Dist(cp, e1p);
            Assert.InRange(e1Dist, 2400f, 3000f);

            // ── The Wingman, already engaged on e1, assigned to defend the
            //    charge (DefendTarget + AttackTarget set directly — simulating
            //    "already chasing a distant one", the exact re-eval precondition).
            Vector3 wp = new(660f, 600f, 640f);
            var wingman = srv.SpawnNpcShip(mission, sector, npcSide, wp, wingDroneId);
            Assert.Equal(Ship.PilotTypeWingman, wingman.PilotType);
            wingman.DefendTarget = charge;
            wingman.AttackTarget = e1;

            // Stabilize: with no closer threat present, the defender keeps e1
            // (the 2500 scan finds nothing nearer, and e1 isn't "running away").
            Pump(srv, () => false, 200);
            Assert.Same(e1, wingman.AttackTarget);
            Console.WriteLine($"P166-CHASING: defender chasing distant e1 ({e1Dist:F0}u from charge)");

            // ── A CLOSER threat appears NEAR the charge: 1200u from it (inside
            //    the 2500 scan radius, and comfortably inside the "1000 closer"
            //    margin vs e1's 2800: 1200 < 2800 - 1000 = 1800).
            Vector3 e2p = new(1800f, 635f, 591f);
            var e2 = new Ship((short)6203, hull, e2p, Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            mission.PlaceShip(e2, sector);
            float e2Dist = Dist(cp, e2p);
            Assert.True(e2Dist < e1Dist - 1000f, $"e2 must be >1000 closer to the charge than e1: e2={e2Dist:F0} e1={e1Dist:F0}");

            // Switch: the re-evaluation finds e2 as the nearest enemy within 2500
            // of the charge and inside the switch margin → AttackTarget = e2.
            Pump(srv, () => wingman.AttackTarget == e2, 400);
            Assert.Same(e2, wingman.AttackTarget);
            Assert.NotSame(e1, wingman.AttackTarget);
            Assert.Same(charge, wingman.DefendTarget);
            Console.WriteLine($"P166-SWITCH: defender switched e1 → e2 ({e2Dist:F0}u from charge)");
        }
    }
}
