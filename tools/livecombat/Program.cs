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
/// The P22 two-bot live-combat demo: an in-process fightable <see cref="Srv"/>
/// server plus two real <see cref="Bot"/> instances driven exactly as the
/// shipped <c>allegiance.bot</c> console app drives them. The attacker commands
/// <c>AllWeapons</c> and holds nose-to-nose; the target idles; the server ticks
/// real chunks until a real <c>KILL_SHIP(105)</c> is broadcast and the target
/// bot observes it via <see cref="Bot.Kills"/> (the new surface P22 adds).
/// Prints the verbatim kill evidence, matching P19's style.
/// </summary>
internal static class Program
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";
    private const float TargetZ = -180f;

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

    private static void InjectFightSpawn(Srv server)
    {
        var placedShooter = false;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (hull, weapon) = Fight.Value;
            var pos = !placedShooter ? Vector3.Zero : new Vector3(0f, 0f, TargetZ);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, mission.Sectors[0]);   // sets ship.Mission/Sector (MountWeapon needs it)
            if (!placedShooter)
            {
                ship.Energy = hull.MaxEnergy * 100f;       // sustain fire: nothing recharges energy yet (slice)
                ship.MountWeapon(weapon);
            }
            placedShooter = true;
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
            Console.Error.WriteLine("  boots a fightable in-process Allegiance server (port 0 = ephemeral),");
            Console.Error.WriteLine("  spawns two real Bot instances (attacker: AllWeapons; target: idle), and");
            Console.Error.WriteLine("  prints the real KILL_SHIP(105) each bot observes. Requires the stock");
            Console.Error.WriteLine($"  artwork tree at {ArtRoot}.");
            return 2;
        }

        using var server = new Srv();
        InjectFightSpawn(server);
        server.Boot(Config(port));
        ushort bound = server.BoundPort;
        Console.WriteLine($"[demo] server listening on 127.0.0.1:{bound}");

        // ── two real bots, driven exactly as the console app drives them ──
        using var attacker = new BotClass();
        using var target = new BotClass();
        attacker.Connect(bound, "ATTACKER");
        target.Connect(bound, "TARGET");
        attacker.SendLogonRequest(Request("ATTACKER"));
        target.SendLogonRequest(Request("TARGET"));

        PumpUntil(server, () => attacker.ShipAssignment is not null && target.ShipAssignment is not null, 4000);

        var aAck = attacker.WaitForAck(2000);
        var tAck = target.WaitForAck(2000);
        if (aAck is null || tAck is null || !aAck.FValidated || !tAck.FValidated)
        {
            Console.Error.WriteLine("demo FAILED: both bots must logon validated");
            return 1;
        }
        var aSet = attacker.ShipAssignment ?? throw new InvalidOperationException("no attacker ship");
        var tSet = target.ShipAssignment ?? throw new InvalidOperationException("no target ship");

        // Resolve roles by spawn position (first-spawned sits at origin): the two
        // sockets race on which logon the server drains first, so position, not
        // connection order, decides who is the shooter.
        var aAtOrigin = aSet.Ship.Position.Z > -1f;
        var shooter = aAtOrigin ? attacker : target;
        var victim = aAtOrigin ? target : attacker;
        short shooterId = (aAtOrigin ? aSet : tSet).Ship.ShipId;
        short targetId = (aAtOrigin ? tSet : aSet).Ship.ShipId;
        Console.WriteLine($"[demo] shooter bot ship {shooterId} at {shooter.ShipAssignment!.Ship.Position}, " +
                          $"target bot ship {targetId} at {victim.ShipAssignment!.Ship.Position}");
        Console.WriteLine($"[demo] shooter commands AllWeapons (state=0x{ShipControlState.AllWeapons:X}), holds nose-to-nose; target idles");

        shooter.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);
        victim.CommandControls(ControlData.Neutral, 0);

        // Drive the real server until the TARGET bot observes its own death on
        // the wire (KILL_SHIP ShipId == its ship), with a sane timeout.
        const int MaxTicks = 900;
        var start = DateTime.UtcNow;
        int ticks = PumpUntil(server, () => victim.Kills.Any(k => k.ShipId == targetId), MaxTicks);
        var elapsed = DateTime.UtcNow - start;

        var victimKills = victim.Kills;
        var shooterKills = shooter.Kills;
        var victimKill = victimKills.FirstOrDefault(k => k.ShipId == targetId);
        if (victimKill is null)
        {
            Console.Error.WriteLine($"demo FAILED: no KILL_SHIP within {MaxTicks} ticks ({elapsed.TotalSeconds:F1}s)");
            return 1;
        }

        Console.WriteLine("[demo] === KILL OBSERVED BY REAL BOT/console-app path ===");
        Console.WriteLine($"[demo] target bot {targetId} observed its kill over the wire after {ticks} ticks ({elapsed.TotalSeconds:F1}s sim-paced)");
        Console.WriteLine($"[demo] KILL_SHIP(105) on target bot: shipID={victimKill.ShipId} typeCredit={victimKill.TypeCredit} " +
                          $"idCredit={victimKill.IdCredit} sideKiller={victimKill.SideIdKiller} bKill={victimKill.BKillCredit} bDeath={victimKill.BDeathCredit}");
        Console.WriteLine($"[demo] KILL_SHIP(105) bytes = {BitConverter.ToString(victimKill.Encode())}");

        var shooterKill = shooterKills.FirstOrDefault(k => k.ShipId == targetId);
        Console.WriteLine($"[demo] shooter bot observed {(shooterKill is null ? "no kill" : "the same kill")}" +
                          $"{(shooterKill is null ? "" : $" credited to idCredit={shooterKill.IdCredit}")}");
        if (victimKill.BDeathCredit && victimKill.ShipId == targetId) return 0;
        return 1;
    }
}
