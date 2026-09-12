using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using BotClass = global::Allegiance.Bot.Bot;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Tools.LiveCombat;

/// <summary>
/// The P24 N-bot live-combat demo: an in-process fightable <see cref="Srv"/>
/// plus N independent 1v1 lanes, each a pair of real <see cref="Bot"/>
/// instances (2N real TCP connections total) driven exactly as the console
/// app drives one. Combat fire is straight-line by design — a ship's weapon
/// fires along its own heading, there is no aim-assist — so each pair sits in
/// its own lane (staggered on X, same Z-facing arrangement as P22) so every
/// shooter's straight shot only ever reaches its own paired target. The P24
/// generalization of P22's two-bot demo: same proven weapon, same energy
/// boost, N times, then a per-lane kill report.
/// </summary>
internal static class Program
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";
    private const float TargetZ = -180f;         // target is this far -Z from its shooter (unchanged from P22)
    private const float LaneSpacing = 432f;      // lanes staggered on X: this gap keeps shots lane-isolated
    private const int Pairs = 4;                 // N = 4 → 8 real bot connections. See README/report for rationale.

    // A hull that can actually fight (energy+ammo and a fixed hardpoint) paired
    // with the cheapest dispersion-0 weapon whose projectile does real damage
    // (Power > 0) — the same requirements LiveCombatWireTests enforces, because
    // the stock server.json default spawn ("Adv Miner") can never fire.
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon)> Fight = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var hull in core.HullTypes.Values
                     .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                     .OrderBy(h => h.HullId))
        {
            foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                         .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                         .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                         .OrderBy(w => w.EnergyPerShot))
            {
                if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Power > 0f)
                    return (hull, p);
            }
        }
        throw new InvalidOperationException("no fightable hull+weapon in the representative core");
    });

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    /// <summary>
    /// Generalizes P22's two-ship binary switch to an N-pair indexed spawner.
    /// Global spawn index i (0..2N-1) maps to lane i/2 and role
    /// (i even → armed shooter at lane origin, i odd → idle target at the same
    /// x, TargetZ behind). Because the server drains the 2N sockets' logons in
    /// a racing order, position — not connection order — decides who is the
    /// shooter (the same rule P22 used for its single pair); the geometry
    /// guarantee is structural: every lane ends up with exactly one armed
    /// ship at z≈0 and one unarmed ship at the same x, z=-180.
    /// </summary>
    private static void InjectFightSpawn(Srv server)
    {
        var placed = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (hull, weapon) = Fight.Value;
            int lane = placed / 2;
            bool shooter = (placed % 2) == 0;    // P22's binary switch, now lane-indexed
            var pos = new Vector3(lane * LaneSpacing, 0f, shooter ? 0f : TargetZ);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, mission.Sectors[0]);   // sets ship.Mission/Sector (MountWeapon needs it)
            if (shooter)
            {
                ship.Energy = hull.MaxEnergy * 100f;       // sustain fire: nothing recharges energy yet (slice)
                ship.MountWeapon(weapon);                   // the same proven weapon, mounted like P22 (mount 0)
            }
            placed++;
            return (ship, mission.Sectors[0]);
        };
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Combat Demo", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 910u, DwCookie = 0xC0FFEEu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 75000000000000000ul, DrmHash = "livecombat",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static int Main(string[] args)
    {
        int port = 0;
        if (args.Length > 0 && !int.TryParse(args[0], out port))
        {
            Console.Error.WriteLine("usage: livecombat [port]");
            Console.Error.WriteLine($"  boots a fightable in-process Allegiance server (port 0 = ephemeral),");
            Console.Error.WriteLine($"  spawns {Pairs * 2} real Bot instances across {Pairs} independent 1v1 lanes,");
            Console.Error.WriteLine($"  and prints the real KILL_SHIP(105) each lane's target bot observes.");
            Console.Error.WriteLine($"  Requires the stock artwork tree at {ArtRoot}.");
            return 2;
        }

        // Diagnostic: the representative weapon's decoded straight-line range
        // (Speed × Lifespan, Projectile.cs:56) — proof TargetZ=180 is within
        // range and lanes are comfortably spaced, not assumed.
        var rCore = RepresentativeCore();
        var fxPt = rCore.ProjectileTypes[Fight.Value.weapon.ProjectileTypeId];
        float fxFiringRange = fxPt.Speed * fxPt.Lifespan;
        Console.WriteLine($"[demo] weapon projectile {fxPt.ProjectileTypeId}: Speed={fxPt.Speed} Lifespan={fxPt.Lifespan} → straight range {fxFiringRange:F0}");
        Console.WriteLine($"[demo] TargetZ={TargetZ} ≪ range ({TargetZ:F0} < {fxFiringRange:F0}); LaneSpacing={LaneSpacing} → X lanes never converge, shots stay in-lane");

        using var server = new Srv();
        InjectFightSpawn(server);
        server.Boot(Config(port));
        ushort bound = server.BoundPort;
        Console.WriteLine($"[demo] server listening on 127.0.0.1:{bound}");
        Console.WriteLine($"[demo] spawning {Pairs} pairs = {Pairs * 2} real Bot instances, {Pairs} lanes");

        // ── 2N real bots, driven exactly as the console app drives them ──
        var shooters = new BotClass[Pairs];
        var targets = new BotClass[Pairs];
        for (int k = 0; k < Pairs; k++)
        {
            shooters[k] = new BotClass();
            targets[k] = new BotClass();
        }
        for (int k = 0; k < Pairs; k++)
        {
            shooters[k].Connect(bound, $"S{k}");
            targets[k].Connect(bound, $"T{k}");
        }
        for (int k = 0; k < Pairs; k++)
        {
            shooters[k].SendLogonRequest(Request($"SHOOTER{k}"));
            targets[k].SendLogonRequest(Request($"TARGET{k}"));
        }

        // Wait for every ship assignment (all 2N spawns), then validate acks.
        PumpUntil(server, () => shooters.All(s => s.ShipAssignment is not null) && targets.All(t => t.ShipAssignment is not null), 4000);
        var assignments = shooters.Select(s => s.ShipAssignment).Concat(targets.Select(t => t.ShipAssignment)).ToArray();
        if (assignments.Any(a => a is null))
        {
            Console.Error.WriteLine("demo FAILED: not all bots were assigned a ship");
            return 1;
        }

        // ── classify the 2N raced spawns into lanes by POSITION (P22's rule, N times) ──
        // lane   = round(X / LaneSpacing)  ;  role decided by Z sign (shooter ≈ 0, target = -180).
        var shooterByLane = new Dictionary<int, (BotClass bot, SetCluster asn)>();
        var targetByLane = new Dictionary<int, (BotClass bot, SetCluster asn)>();
        foreach (var s in shooters.Concat(targets).Select((b, i) => (b, i)))
        {
            var asn = s.b.ShipAssignment!;
            int lane = (int)MathF.Round(asn.Ship.Position.X / LaneSpacing);
            bool isShooter = asn.Ship.Position.Z > -1f;
            var entry = (s.b, asn);
            if (isShooter) shooterByLane[lane] = entry; else targetByLane[lane] = entry;
        }

        // Integrity check: exactly one armed shooter and one idle target per lane.
        int bad = 0;
        for (int k = 0; k < Pairs; k++)
        {
            bool ok = shooterByLane.TryGetValue(k, out var st) && targetByLane.TryGetValue(k, out var tg);
            if (!ok) { Console.Error.WriteLine($"demo FAILED: lane {k} missing a shooter or target"); bad++; }
        }
        if (bad > 0) return 1;

        // ── command every lane's shooter: AllWeapons; every target: idle ──
        for (int k = 0; k < Pairs; k++)
        {
            var st = shooterByLane[k];
            var tg = targetByLane[k];
            Console.WriteLine($"[demo] lane {k}: shooter ship {st.asn.Ship.ShipId} at {st.asn.Ship.Position}, " +
                              $"target ship {tg.asn.Ship.ShipId} at {tg.asn.Ship.Position}");
        }
        Console.WriteLine($"[demo] all {Pairs} shooters command AllWeapons (state=0x{ShipControlState.AllWeapons:X}), all targets idle");

        for (int k = 0; k < Pairs; k++)
            shooterByLane[k].bot.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);
        for (int k = 0; k < Pairs; k++)
            targetByLane[k].bot.CommandControls(ControlData.Neutral, 0);

        // Drive the real server until EVERY lane's target bot observes its own
        // death on the wire (KILL_SHIP ShipId == its ship), with a sane timeout.
        const int MaxTicks = 1200;
        var start = DateTime.UtcNow;
        bool allDeadDone() => Enumerable.Range(0, Pairs).All(k => targetByLane[k].bot.Kills.Any(kill => kill.ShipId == targetByLane[k].asn.Ship.ShipId));
        int ticks = PumpUntil(server, allDeadDone, MaxTicks);
        var elapsed = DateTime.UtcNow - start;

        // ── per-lane evidence, P19/P22 style ──
        int landed = 0;
        Console.WriteLine("[demo] === KILLS OBSERVED BY REAL BOTS (per lane) ===");
        Console.WriteLine($"[demo] server ticked {ticks} times, {elapsed.TotalSeconds:F1}s sim-paced");
        for (int k = 0; k < Pairs; k++)
        {
            var tg = targetByLane[k];
            var tId = tg.asn.Ship.ShipId;
            var kill = tg.bot.Kills.FirstOrDefault(x => x.ShipId == tId);
            if (kill is null)
            {
                Console.WriteLine($"[lane {k}] target ship {tId} observed NO kill within {MaxTicks} ticks");
                continue;
            }
            landed++;
            Console.WriteLine($"[lane {k}] shooter ship {shooterByLane[k].asn.Ship.ShipId} → target ship {tId} KILLED over the wire");
            Console.WriteLine($"[lane {k}] KILL_SHIP(105) on target bot: shipID={kill.ShipId} typeCredit={kill.TypeCredit} " +
                              $"idCredit={kill.IdCredit} sideKiller={kill.SideIdKiller} bKill={kill.BKillCredit} bDeath={kill.BDeathCredit}");
            Console.WriteLine($"[lane {k}] KILL_SHIP(105) bytes = {BitConverter.ToString(kill.Encode())}");
            var sKill = shooterByLane[k].bot.Kills.FirstOrDefault(x => x.ShipId == tId);
            Console.WriteLine($"[lane {k}] shooter bot observed {(sKill is null ? "no kill" : "the same kill")}" +
                              $"{(sKill is null ? "" : $" credited to idCredit={sKill.IdCredit}")}");
        }

        Console.WriteLine($"[demo] RESULT: {landed}/{Pairs} lanes scored a real KILL_SHIP, all in the same server run");
        if (landed == Pairs && Enumerable.Range(0, Pairs).All(k => targetByLane[k].bot.Kills.Any(x => x.ShipId == targetByLane[k].asn.Ship.ShipId && x.BDeathCredit)))
            return 0;
        return 1;
    }
}
