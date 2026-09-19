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
/// P266 — live loopback proof of the predictive LEAD indicator over the REAL
/// wire. A real in-process server (PCore001.igc) seats a real bot whose own ship
/// is a fighter hull carrying <c>c_habmLeadIndicator</c> (0x800) with a mounted
/// weapon; an enemy ship is placed in the same sector and driven LATERALLY
/// (kinematically, so its motion is exactly prescribed). Setting the fighter's
/// command target to that moving enemy makes the server's per-tick
/// <c>BroadcastLeadIndicator</c> solve <c>solveForLead</c> (Ship.SolveLead) and
/// stream the owner-only <c>LEAD_INDICATOR_UPDATE(210)</c> to the bot. The test
/// asserts the received lead point is GENUINELY OFFSET from the target's current
/// position (a real predictive lead swung toward the target's motion, not drawn
/// on the target itself), and that clearing the command target clears the
/// indicator. Fails if the broadcast wiring or the lead solve is removed.
/// </summary>
[Collection("LiveNetwork")]
public sealed class LeadIndicatorLiveTests
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
            new Gm { Name = "P266 Lead Indicator Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 556u, DwCookie = 0x556u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 55300000000000005ul, DrmHash = "p266lead",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A fightable weapon whose projectile is the SLOWEST in the core,
    /// maximizing the lateral lead angle (and thus the offset) for a given target
    /// speed — the cleanest way to prove the lead is real and large.</summary>
    private static (DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC proj) SlowestLeadFighter(StaticCore core)
    {
        DataPartTypeIGC.Weapon? best = null;
        DataProjectileTypeIGC bp = default;
        float bs = float.MaxValue;
        foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                     .Select(p => (DataPartTypeIGC.Weapon)p.Type))
        {
            if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Speed > 0f && pt.Speed < bs)
            { bs = pt.Speed; best = p; bp = pt; }
        }
        if (best is null) throw new InvalidOperationException("no weapon with a projectile in the representative core");

        // A fighter hull that carries c_habmLeadIndicator (the direct gate) and
        // can mount the weapon's energy.
        var hull = core.HullTypes.Values
            .Where(h => h.HasCapability(DataHullTypeIGC.HabmLeadIndicator)
                        && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed
                        && h.MaxEnergy >= best!.EnergyPerShot)
            .OrderBy(h => h.HullId)
            .FirstOrDefault();
        if (hull == default)
            throw new InvalidOperationException("no HabmLeadIndicator fightable hull in the representative core");
        return (hull, best, bp);
    }

    /// <summary>⭐ Live proof: a fighter whose hull carries c_habmLeadIndicator
    /// with a mounted (slow-projectile) weapon, given a laterally-MOVING enemy as
    /// its command target, receives a REAL LEAD_INDICATOR_UPDATE(210) whose lead
    /// point is GENUINELY OFFSET from the target's current position — swung toward
    /// the target's motion by the real solveForLead math. Clearing the command
    /// target clears the indicator (retargeting = none). Fails if the per-tick
    /// broadcast or the lead solve is removed.</summary>
    [Fact]
    public void Moving_enemy_command_target_yields_a_real_offset_lead_over_the_wire()
    {
        var server = new Srv();
        Ship? shooter = null;
        short peerSide = 0, npcSide = 0;
        Sector? sector = null;
        (DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC proj) chosen = default;

        server.ShipSpawner = (_, m, id) =>
        {
            sector = m.Sectors[0];
            var core = m.Core;
            peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;
            chosen = SlowestLeadFighter(core);
            var ship = new Ship(id, chosen.hull, new Vector3(985f, 445f, 528f), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            ship.Energy = chosen.hull.MaxEnergy * 100f;
            m.PlaceShip(ship, sector!);
            ship.MountWeapon(chosen.weapon);   // needs the mission (place first) to resolve the projectile type
            shooter = ship;
            return (ship, sector);
        };

        using (server)
        {
            server.Boot(Config());
            foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby
            var bot = new FltBot();
            bot.Connect(server.BoundPort, "p266_lead");
            bot.SendLogonRequest(Request("p266_lead"));
            Pump(server, () => shooter is not null && bot.ShipAssignment is not null, 4000);

            Assert.NotNull(shooter); Assert.NotNull(sector);
            Assert.True(chosen.hull != default, "no fightable HabmLeadIndicator hull");
            Assert.True(chosen.hull.HasCapability(DataHullTypeIGC.HabmLeadIndicator),
                "the chosen hull must carry c_habmLeadIndicator for the direct gate");

            // Place the ENEMY target in the same sector, initially stationary.
            var enemyHull = server.Missions[0].Core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmLeadIndicator));
            Vector3 ep = new(1500f, 447f, 526f);
            var enemy = new Ship((short)6001, enemyHull, ep, Vector3.Zero, Orientation.Identity) { SideId = npcSide };
            server.Missions[0].PlaceShip(enemy, sector!);
            shooter!.CommandTarget = enemy;

            // Drive the enemy laterally (+Y) at a brisk clip; the slow projectile
            // turns that into a large, unambiguous lead offset.
            float latSpeed = 140f;
            Vector3 lat = new(0f, latSpeed, 0f);
            const double dt = 0.080;   // step the lane coarsely; offset is what matters
            double elapsed = 0;
            int ticks = 0; const int MaxTicks = 1200;

            LeadIndicatorUpdate? seen = null;
            while (ticks < MaxTicks)
            {
                elapsed += dt;
                enemy.Position = ep + lat * (float)elapsed;
                enemy.Velocity = lat;
                server.TickSim();
                ticks++; Thread.Sleep(1);

                var snaps = bot.LeadIndicatorBroadcasts;
                if (snaps.Count > 0) seen = snaps[^1];
                if (seen is { HasLead: true }) break;
            }

            Assert.NotNull(seen);
            Assert.True(seen!.HasLead, $"a lead indicator must be emitted for a moving command target (got HasLead=false over {ticks} ticks)");
            Assert.Equal(enemy.Id, seen.TargetShipId);

            // GENUINE OFFSET: the lead world point differs from the target's current
            // position, swung toward the target's +Y motion — the predictive lead,
            // not a marker drawn on the target itself.
            Vector3 off = seen.LeadWorldPos - seen.TargetPos;
            float offLen = off.Length();
            Console.WriteLine(
                $"P266-LIVE: projSpeed={chosen.proj.Speed:F1}, projAbs={chosen.proj.AbsoluteF}, targetSpeed={latSpeed:F0}, " +
                $"leadWorld=({seen.LeadWorldPos.X:F0},{seen.LeadWorldPos.Y:F0},{seen.LeadWorldPos.Z:F0}), " +
                $"targetPos=({seen.TargetPos.X:F0},{seen.TargetPos.Y:F0},{seen.TargetPos.Z:F0}), " +
                $"offset={offLen:F1} units, inRange={(seen.InRange ? 1 : 0)}, green={seen.Green:F2}");

            Assert.True(offLen > 15f,
                $"the lead must be genuinely offset from the target: |lead - target|={offLen:F1} (> 15)");
            Assert.True(off.Y > 0f,
                $"the lead must swing toward the target's +Y motion: off.Y={off.Y:F1} (> 0)");
            Assert.InRange(seen.Green, 0f, 1f);

            // Retarget to nothing → the indicator clears (NA target).
            shooter.CommandTarget = null;
            Pump(server, () => bot.LeadIndicatorBroadcasts.Count > 0 && !bot.LeadIndicatorBroadcasts[^1].HasLead, 400);
            Assert.False(bot.LeadIndicatorBroadcasts[^1].HasLead,
                "clearing the command target must clear the lead indicator over the wire");
        }
    }
}
