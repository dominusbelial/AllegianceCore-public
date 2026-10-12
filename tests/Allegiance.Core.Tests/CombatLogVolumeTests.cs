using System.Reflection;
using System.Text;
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
/// P619c scope 3 — the log-VOLUME harness for the COMBAT hot tags. A real
/// (loopback) in-process server seats two same-side allies plus a cross-side
/// enemy in one sector, arms the subject with a real weapon and tops up its
/// pools (the <c>CombatShieldTests.ArmShooter</c> recipe) so the server's
/// authoritative sim sustains many shots, then measures the per-tick combat
/// diagnostics that used to flood Docker stdout:
/// <c>[projectile]</c>, <c>[projectile-hit]</c>, <c>[xexport]</c>,
/// <c>[teamloadout]</c>, <c>[lead]</c>, <c>[orderchange]</c>.
/// <para>
/// With the verbosity gate OFF (production default) all six must be silent —
/// fails on today's pre-P619c code, where <c>[projectile]</c> etc. were
/// unconditional <c>Console.WriteLine</c> and appear on stdout no matter the
/// gate. With the gate ON the same workload reproduces them (the gate is a real
/// switch, not a broken no-op).
/// </para>
/// </summary>
[Collection("LiveNetwork")]
public class CombatLogVolumeTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(bool verbose) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        ConsoleVerbose = verbose,
        Games =
        [
            new Gm { Name = "CombatLogVolume", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 613u, DwCookie = 0x613u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 63000000000000000ul, DrmHash = "clv619c",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Srv Boot(bool verbose)
    {
        int placed = 0;
        var server = new Srv();
        var spawnerField = typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ShipSpawner field not found");
        spawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var sector = mission.Sectors[0];
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var side = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin
            var ship = new Ship(id, hull, new Vector3(placed * 250f, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = side };
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        });
        server.Boot(Config(verbose));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        return server;
    }

    private static (DataPartTypeIGC.Weapon w, short projectileTypeId) RealWeapon(StaticCore core)
    {
        foreach (var w in core.PartTypes.Values
                     .Where(p => p.EquipmentType == EquipmentType.Weapon)
                     .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                     .Where(w => w.Dispersion == 0f)
                     .OrderBy(w => w.EnergyPerShot))
        {
            if (!core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var pt) || pt.Power <= 0f) continue;
            return (w, w.ProjectileTypeId);
        }
        throw new InvalidOperationException("no dispersion-0 weapon with a positive-power projectile in the representative core");
    }

    private static readonly string[] HotTags =
        { "[projectile]", "[projectile-hit]", "[xexport]", "[teamloadout]", "[lead]", "[orderchange]" };

    /// <summary>⭐ Combats with the gate OFF emit ZERO of the six hot combat
    /// tags; with the gate ON the same workload reproduces them.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Combat_log_tags_are_silent_gate_off_and_present_gate_on()
    {
        Require.Artwork();
        // ── gate OFF ──────────────────────────────────────────────────────
        string offText;
        var offOut = new StringWriter();
        var oldOut = Console.Out;
        try
        {
            using var server = Boot(verbose: false);
            var bots = SeatAndArm(server, out var shooter);
            FireSalvo(server, shooter);
            Console.SetOut(offOut);
            FireSalvo(server, shooter);            // salvo while capturing
            ConsoleLog.Flush();
            Console.SetOut(oldOut);
            offText = offOut.ToString();
            foreach (var b in bots) b.Dispose();
        }
        finally { Console.SetOut(oldOut); ConsoleLog.ResetForTest(); }

        foreach (var tag in HotTags)
            Assert.DoesNotContain(tag, offText);

        // ── gate ON ───────────────────────────────────────────────────────
        string onText;
        var onOut = new StringWriter();
        try
        {
            using var server = Boot(verbose: true);
            var bots = SeatAndArm(server, out var shooter);
            Console.SetOut(onOut);
            FireSalvo(server, shooter);
            ConsoleLog.Flush();
            Console.SetOut(oldOut);
            onText = onOut.ToString();
            foreach (var b in bots) b.Dispose();
        }
        finally { Console.SetOut(oldOut); ConsoleLog.ResetForTest(); }

        // The gate must be a REAL switch: the same workload with it ON must emit
        // at least the projectile fire line (the deterministic combat tag).
        Assert.Contains("[projectile]", onText);
    }

    private static List<FltBot> SeatAndArm(Srv server, out Ship shooter)
    {
        var bots = new List<FltBot>();
        for (int i = 0; i < 3; i++)
        {
            var b = new FltBot();
            b.Connect(server.BoundPort, $"clv{i}");
            b.SendLogonRequest(Request($"clv{i}"));
            bots.Add(b);
        }
        PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
        Assert.True(bots.All(b => b.ShipAssignment is not null), "all bots seated");

        var live = server.Ships.First();
        shooter = live.Ship;
        var mission = shooter.Mission ?? throw new InvalidOperationException("shooter must have a mission");
        var (weapon, _) = RealWeapon(mission.Core);
        shooter.StateM |= ShipControlState.AllWeapons;
        shooter.Energy = 1_000_000f;
        shooter.Ammo = 30_000;
        shooter.MountWeapon(weapon);
        return bots;
    }

    private static void FireSalvo(Srv server, Ship shooter)
    {
        _ = shooter;
        // Pump real ticks so the server's authoritative sim fires the mounted
        // weapon repeatedly (many [projectile] broadcasts in this sector).
        for (int i = 0; i < 300; i++) server.TickSim();
    }
}
