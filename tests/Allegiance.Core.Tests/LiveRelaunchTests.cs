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
/// P117 — the real <c>RELAUNCH_SHIP</c>(192) effect, the port of the native
/// carrier-branch effect of <c>CshipIGC::HandleCollision</c>
/// (<c>shipIGC.cpp:1038-1044</c>) and <c>FedSrvSiteBase::LandOnCarrierEvent</c>
/// (<c>FedSrv.CPP:12808-13031</c>): a real lander that lands on a real carrier
/// is resupplied at ship level (energy+ammo restored to hull maxima), moved to
/// a real launch slot (or the backward fallback when the carrier has no launch
/// frames), and <c>RELAUNCH_SHIP</c>(192) is broadcast to everyone flying in
/// the sector. Real loopback Bots vs an in-process Server, exactly the
/// <see cref="LiveRescueTests"/> recipe.
///
/// Geometry (measured this task, real corpus): <b>cap502</b> has two real
/// <c>launch1</c>/<c>launch2</c> frames at carrier-local x≈±42.8, y≈−21.8,
/// z≈−49.6, dir (0,0,−1), with a landing bay whose interior contains the
/// carrier-local point (−60,−32,70) for a fig22 lander. <b>faohshcar</b> has
/// a single <c>launch</c> frame (⇒ 0 launch slots) and a bay interior at
/// (−10,−26,36). Both offsets are in carrier-local space with the carrier at
/// identity orientation (the spawn default here).
///
/// Scenarios (all real corpus hulls + real .cvh geometry, served through the
/// production artPath cache the live server installs):
///   (1) cap502: depleted lander lands → energy/ammo restored, RELAUNCH_SHIP(192)
///       with correct shipID/carrierID to both bots, lander repositioned to a
///       real launch slot, and no longer InGarage on the very next tick
///       (natural self-termination);
///   (2) cap502 again: consecutive landings round-robin across the two slots
///       (carrier's GetLaunchSlot modulo counter);
///   (3) faohshcar: zero launch slots → the fallback backward-offset branch;
///   (4) after a successful relaunch, further ticks produce no repeat
///       detection/relaunch (the lander is genuinely out of the bay).
/// </summary>
[Collection("LiveNetwork")]
public class LiveRelaunchTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // XXV_simple (measured, P111): 1052 = no-station fly sector.
    private const short FlySector = 1052;

    // Real carrier-local inside points for the fig22 lander (radius 5.3).
    private static readonly Vector3 Cap502Inside = new(-60f, -32f, 70f);   // cap502's slot-0 bay (measured)
    private static readonly Vector3 FaohshcarInside = new(-10f, -26f, 36f); // faohshcar's bay (measured)

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Relaunch Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 761u, DwCookie = 0x761u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 7700000000000000000ul, DrmHash = "rlch",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Ship ServerShip(Srv server, short id) =>
        server.Ships.First(s => s.Ship.Id == id).Ship;

    private sealed class RelaunchSpawn
    {
        public Ship Carrier = null!, Lander = null!;
    }

    /// <summary>Two-ship spawn injection: the first spawned ship (bot A) is the
    /// carrier hull <paramref name="carrierModel"/>, the second (bot B) is a
    /// dreghpod cloned with exactly <c>c_habmLandOnCarrier</c>. Placed far
    /// apart in the no-station fly sector so no landing triggers during the
    /// join pump (the test positions the lander afterwards).</summary>
    private static RelaunchSpawn Inject(
        Srv server, string carrierModel,
        Vector3 carrierPos, Vector3 landerPos,
        short carrierSide = 0, short landerSide = 0)
    {
        var sp = new RelaunchSpawn();
        int n = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            if (n == 0)
            {
                sp.Carrier = new Ship(shipId,
                    core.HullTypes.Values.First(h => h.ModelName == carrierModel),
                    carrierPos, Vector3.Zero, Orientation.Identity) { SideId = carrierSide };
                mission.PlaceShip(sp.Carrier, fly);
            }
            else
            {
                var fig = core.HullTypes.Values.First(h => h.ModelName == "fig22");
                sp.Lander = new Ship(shipId,
                    fig with { HabmCapabilities = DataHullTypeIGC.HabmLandOnCarrier },
                    landerPos, Vector3.Zero, Orientation.Identity) { SideId = landerSide };
                mission.PlaceShip(sp.Lander, fly);
            }
            n++;
            return n == 1 ? (sp.Carrier!, fly) : (sp.Lander!, fly);
        };
        return sp;
    }

    private static (Srv server, FltBot carBot, FltBot lanBot, RelaunchSpawn sp)
        Setup(string carrierModel, Vector3 carrierPos)
    {
        var server = new Srv();
        // Lander spawns far from the carrier so the join pump never relaunches prematurely.
        var sp = Inject(server, carrierModel, carrierPos, carrierPos + new Vector3(0f, 400f, 0f));
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        var a = new FltBot(); var b = new FltBot();
        a.Connect(server.BoundPort, "CRRR"); b.Connect(server.BoundPort, "LNDR");
        a.SendLogonRequest(Request("CRRR")); b.SendLogonRequest(Request("LNDR"));
        PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);

        Assert.NotNull(sp.Carrier);
        Assert.NotNull(sp.Lander);
        Assert.Null(sp.Lander.StationId);
        Assert.Null(sp.Carrier.StationId);
        Assert.Equal(FlySector, sp.Lander.Sector!.Id);
        Assert.Equal(FlySector, sp.Carrier.Sector!.Id);
        a.ResetRelaunchShips(); b.ResetRelaunchShips();
        return (server, a, b, sp);
    }

    /// <summary>Park the lander inside the carrier's real bay (carrier-local
    /// inside point + carrier world position, identity carrier orientation).</summary>
    private static void ParkInBay(RelaunchSpawn sp, Vector3 insideOffset)
    {
        sp.Lander.Position = sp.Carrier.Position + insideOffset;
        sp.Lander.Velocity = Vector3.Zero;
        var bay = sp.Carrier.Sector!.LandingBays!.Bay(sp.Carrier.HullType);
        Assert.NotNull(bay);
        Assert.True(sp.Carrier.InGarage(sp.Lander, 0f, bay),
            $"lander at {sp.Lander.Position} not InGarage in {sp.Carrier.HullType.ModelName} bay");
    }

    /// <summary>⭐ Scenario 1 — a real depleted lander genuinely lands on cap502
    /// (two real launch slots): energy/ammo restored to the hull maxima, a real
    /// RELAUNCH_SHIP(192) with the correct shipID/carrierID reaches both bots,
    /// the lander is repositioned to a real launch slot (outside the bay), and
    /// it is no longer InGarage on the very next tick (natural self-termination).</summary>
    [Fact]
    public void Depleted_lander_relaunches_off_carrier_with_slots_and_is_resupplied()
    {
        var (server, carBot, lanBot, sp) = Setup("cap502", new Vector3(500f, 950f, 930f));
        var carrier = sp.Carrier;
        var lander = sp.Lander;

        // Deplete the lander's pools well below the hull maxima.
        lander.Energy = 5f;
        lander.Ammo = 1;
        float maxE = lander.EffectiveMaxEnergy();   // P467b: the refill restores to the GA-scaled effective ceiling
        short maxA = lander.HullType.MaxAmmo;
        var insidePos = lander.Position = carrier.Position + Cap502Inside;

        int seen = PumpUntil(server,
            () => lanBot.RelaunchShips.Count >= 1 && carBot.RelaunchShips.Count >= 1, 4000);
        Assert.True(seen < 4000, "no RELAUNCH_SHIP(192) reached both bots");

        var p = Assert.Single(lanBot.RelaunchShips);
        var c = Assert.Single(carBot.RelaunchShips);
        Assert.Equal(lander.Id, p.ShipId);
        Assert.Equal(carrier.Id, p.CarrierId);
        Assert.Equal(p.ShipId, c.ShipId);
        Assert.Equal(p.CarrierId, c.CarrierId);
        Assert.Equal(0, p.Cookie);                       // no per-player cookie machinery
        Assert.Equal(lander.HullType.HullId, p.HullId);  // loadout blob hull
        Assert.Empty(p.Parts);                           // hull-only lander (no mounted parts)

        // Resupply: both pools restored to their hull maxima.
        Assert.Equal(maxE, ServerShip(server, lander.Id).Energy);
        Assert.Equal(maxA, ServerShip(server, lander.Id).Ammo);

        // Reposition: the lander actually moved out of the bay to a launch slot.
        var moved = ServerShip(server, lander.Id);
        Assert.NotEqual(insidePos, moved.Position);
        Assert.True((moved.Position - carrier.Position).Length() > 5f, "lander did not clear the bay");

        // Self-termination: no longer InGarage on the very next tick (the real geometry test).
        var bay = carrier.Sector!.LandingBays!.Bay(carrier.HullType);
        Assert.False(carrier.InGarage(moved, 0f, bay!), "lander still InGarage after relaunch");

        Console.WriteLine($"RELAUNCH-SLOTS-LIVE: ship {lander.Id} landed on carrier {carrier.Id} (cap502) -> energy {5:F0}->{maxE:F0} ammo {1}->{maxA}; RELAUNCH_SHIP(192) shipID={p.ShipId}/carrierID={p.CarrierId} to BOTH bots; moved to ({moved.Position.X:F0},{moved.Position.Y:F0},{moved.Position.Z:F0}) outside the bay");
    }

    /// <summary>⭐ Scenario 2 — consecutive landings round-robin across cap502's
    /// two real launch slots (the carrier's GetLaunchSlot modulo counter): the
    /// two relaunches use different slots (slot1 then slot0), and further ticks
    /// after each do not re-fire.</summary>
    [Fact]
    public void Consecutive_landings_round_robin_across_launch_slots()
    {
        var (server, carBot, lanBot, sp) = Setup("cap502", new Vector3(900f, 960f, 920f));
        var carrier = sp.Carrier;
        var lander = sp.Lander;
        var carrierX = carrier.Position.X;

        // First landing → slot 1 (counter 0→1).
        ParkInBay(sp, Cap502Inside);
        Assert.True(PumpUntil(server, () => lanBot.RelaunchShips.Count >= 1, 4000) < 4000, "first relaunch missed");
        float x1 = ServerShip(server, lander.Id).Position.X;
        Assert.True(x1 > carrierX + 20f, $"first slot should be +x slot (x1={x1:F0}, carrierX={carrierX:F0})");

        // Second landing → slot 0 (counter 1→0). Repark the lander in the bay.
        lanBot.ResetRelaunchShips();
        ParkInBay(sp, Cap502Inside);
        Assert.True(PumpUntil(server, () => lanBot.RelaunchShips.Count >= 1, 4000) < 4000, "second relaunch missed");
        float x2 = ServerShip(server, lander.Id).Position.X;
        Assert.True(x2 < carrierX - 20f, $"second slot should be -x slot (x2={x2:F0}, carrierX={carrierX:F0})");

        Console.WriteLine($"RELAUNCH-RR-LIVE: cap502 consecutive landings used slot1 (x{x1:F0}) then slot0 (x{x2:F0}) — GetLaunchSlot round-robin ({carrier.NextLaunchSlot} next)");
    }

    /// <summary>⭐ Scenario 3 — a carrier with zero launch slots (faohshcar's
    /// single <c>launch</c> frame, not <c>launch1</c>) uses the native's
    /// backward-offset fallback branch: the lander is thrown straight back
    /// along the carrier's backward axis.</summary>
    [Fact]
    public void Carrier_without_launch_slots_uses_backward_fallback()
    {
        var (server, carBot, lanBot, sp) = Setup("faohshcar", new Vector3(1300f, 970f, 910f));
        var carrier = sp.Carrier;
        var lander = sp.Lander;

        ParkInBay(sp, FaohshcarInside);
        Assert.True(PumpUntil(server, () => lanBot.RelaunchShips.Count >= 1, 4000) < 4000, "no fallback relaunch");

        var moved = ServerShip(server, lander.Id);
        var r = Assert.Single(lanBot.RelaunchShips);
        Assert.Equal(lander.Id, r.ShipId);
        Assert.Equal(carrier.Id, r.CarrierId);

        // faohshcar @ identity: backward = (0,0,1). Native: position -= backward·dist
        // (FedSrv.CPP:12974) → the lander is thrown to carrier.z − (radCarrier+radLander+25),
        // with velocity −backward·50 → (0,0,−50).
        float dz = moved.Position.Z - carrier.Position.Z;
        float expectedDist = 0.5f * carrier.HullType.Length + 0.5f * lander.HullType.Length + 25f;
        Assert.InRange(dz, -expectedDist - 2f, -expectedDist + 2f);
        Assert.InRange(moved.Velocity.Z, -52f, -48f);
        Assert.False(carrier.InGarage(moved, 0f, carrier.Sector!.LandingBays!.Bay(carrier.HullType)!), "lander still InGarage after fallback relaunch");

        Console.WriteLine($"RELAUNCH-FALLBACK-LIVE: ship {lander.Id} landed on faohshcar (0 launch slots) -> backward fallback dz={dz:F1} (~{-expectedDist:F1}) vel z={moved.Velocity.Z:F1} RELAUNCH_SHIP(192) shipID={r.ShipId}/carrierID={r.CarrierId}");
    }

    /// <summary>⭐ Scenario 4 — after a successful relaunch, further ticks produce
    /// no repeat detection/relaunch: the lander was moved out of the bay, so the
    /// per-tick geometric pass finds nothing new. Asserted on the geometry
    /// (the lander stays not-InGarage over many ticks) rather than on a raw
    /// bot message count, which races the bot's asynchronous receive thread.</summary>
    [Fact]
    public void After_relaunch_no_repeat_detection_or_launch()
    {
        var (server, carBot, lanBot, sp) = Setup("cap502", new Vector3(1700f, 956f, 924f));
        var carrier = sp.Carrier;
        var lander = sp.Lander;

        ParkInBay(sp, Cap502Inside);
        int seen = PumpUntil(server, () => lanBot.RelaunchShips.Count >= 1, 4000);
        Assert.True(seen < 4000, "initial relaunch missed");

        var bay = carrier.Sector!.LandingBays!.Bay(carrier.HullType)!;
        // The relaunch moved the lander out of the bay; it must STAY out — the
        // per-tick pass would otherwise re-detect it and fire again.
        Assert.False(carrier.InGarage(ServerShip(server, lander.Id), 0f, bay),
            "lander still InGarage right after relaunch");

        var relaunchCount = lanBot.RelaunchShips.Count;
        for (int i = 0; i < 40; i++)
        {
            server.TickSim();
            Assert.False(carrier.InGarage(ServerShip(server, lander.Id), 0f, bay),
                $"lander drifted back InGarage on tick {i} — a repeat relaunch would fire");
        }

        // Allow any in-flight duplicate delivery to settle; the authoritative
        // server-side relaunch log is a single occurrence (no re-fire).
        Assert.Equal(relaunchCount, lanBot.RelaunchShips.Count);

        Console.WriteLine($"RELAUNCH-NOREPEAT-LIVE: after the relaunch moved ship {lander.Id} out of the bay, 40 further ticks kept it out of {carrier.HullType.ModelName}'s bay (no re-detect, no re-launch)");
    }
}
