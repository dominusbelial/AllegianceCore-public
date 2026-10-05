using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P542 — the LIVE proof that OTHER SHIPS' projectiles are no longer swallowed
/// by the own-shot-echo de-dup. A real in-process server, real TCP transport, and
/// real <see cref="ConnectionSession"/> / <see cref="ClientWorld"/> peers (the
/// same data plane the MonoGame client uses). It reproduces the exact bug
/// condition from Domingo's 221 report: the local player flies the SAME hull +
/// gun as a nearby shooter, actively fires too (so its own-shot tracer is live,
/// modelled by an always-true <see cref="ClientWorld.OwnEchoDropPredicate"/>),
/// and the enemy shoots the SAME projectile type at close range (≤150 u, inside
/// <c>OwnShotEcho.FireOriginRadius</c>).
///
/// It walks each received <c>PROJECTILE_INSTANCE</c> through the new
/// <see cref="ClientWorld.Diag"/> counters — received / dropped-as-own-echo /
/// radius+camera-culled / drawn, split by shooter relation — and pairs them with
/// the server's own <c>[projectile] ship N fired -&gt; n peer(s)</c> broadcast
/// lines. Before the fix the enemy's near-field same-type rounds land in
/// DroppedEnemy (vanishing); after it they are kept and drawn. Gated behind
/// ALLEGIANCE_LIVE_P542_PROOF=1 (multi-second live driver; the routine suite
/// keeps it a documented no-op).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P542RemoteProjectilesLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    private static Cfg Config(string name) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = name, Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name, ulong steam) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 902u, DwCookie = 0x902u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = steam, DrmHash = "p542",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static ConnectionSession Connect(Srv server, string name, ulong steam)
    {
        var s = new ConnectionSession();
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(Request(name, steam));
        return s;
    }

    /// <summary>A thread-safe snapshot of one client's world diag counters (read
    /// under the session gate). <see cref="ClientWorld.DiagnoseDraw"/> is driven
    /// every tick by <see cref="Run"/>, so <c>Diag.Drawn*</c> is cumulative over
    /// the fight window, not a point-in-time sample.</summary>
    private sealed record WorldSnap(ConnectionSession S, short Me, short MySide,
        ClientWorld.ProjectileDiagnostics Diag)
    {
        public static WorldSnap Of(ConnectionSession s)
        {
            lock (s.Gate)
            {
                var w = s.World;
                return new WorldSnap(s, w.MyShipId ?? -1, w.MySideId ?? -1, w.Diag);
            }
        }
    }

    /// <summary>A real dispersion-0 weapon (positive-power projectile) so the
    /// auto-fire projectile velocity is deterministic — the P88 recipe; BOTH
    /// fighters mount the SAME weapon ⇒ the SAME projectile type, exactly the
    /// same-hull / same-default-gun condition from the 221 report.</summary>
    private static DataPartTypeIGC.Weapon RealWeapon(StaticCore core)
    {
        foreach (var w in core.PartTypes.Values
                     .Where(p => p.EquipmentType == EquipmentType.Weapon)
                     .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                     .Where(w => w.Dispersion == 0f)
                     .OrderBy(w => w.EnergyPerShot))
        {
            if (core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var pt) && pt.Power > 0f)
                return w;
        }
        throw new InvalidOperationException("no dispersion-0 weapon with a positive-power projectile in the representative core");
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly StringBuilder _sb = new();
        public override Encoding Encoding => Encoding.UTF8;
        public TeeWriter(TextWriter inner) => _inner = inner;
        public override void Write(char value) { _inner.Write(value); lock (_sb) _sb.Append(value); }
        public string Captured { get { lock (_sb) return _sb.ToString(); } }
    }

    /// <summary>Count the server's own <c>[projectile] ship N fired ... -&gt; n peer(s)</c>
    /// lines that NAME the given shooter and reached at least one peer.</summary>
    private static int ServerShotsToward(string log, short shooter)
    {
        var rx = new Regex(@"\[projectile\] ship " + shooter + @" fired .*-> (\d+) peer\(s\)");
        int n = 0, toMe = 0;
        foreach (Match m in rx.Matches(log)) { n++; if (int.Parse(m.Groups[1].Value) >= 1) toMe++; }
        return n;
    }

    private static int Run(double windowSeconds)
    {
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_LIVE_P542_PROOF") != "1")
        {
            Console.WriteLine("[P542] live projectile proof SKIPPED — set ALLEGIANCE_LIVE_P542_PROOF=1 to run");
            return 0;
        }
        if (double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_LIVE_P542_SECONDS"), out var ov) && ov > 0)
            windowSeconds = ov;

        var tee = new TeeWriter(Console.Out);
        var saved = Console.Out;
        try
        {
            using var server = new Srv();
            int placed = 0;
            Sector? s0 = null; Vector3 bravoSpawn = default;
            SetShipSpawner(server, (core, mission, id) =>
            {
                var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
                s0 = mission.Sectors[0];
                if (placed == 0)   // Alpha (the local player) at the origin
                {
                    placed++;
                    var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = mission.Sides[0].Id };
                    mission.PlaceShip(ship, s0);
                    return (ship, s0);
                }
                // Bravo (the enemy shooter) 120 u away on +X — inside weapon
                // range AND inside OwnShotEcho.FireOriginRadius (150), so the
                // old de-dup would swallow Bravo's same-type rounds near Alpha.
                bravoSpawn = new Vector3(120f, 0f, 0f);
                var bs = new Ship(id, hull, bravoSpawn, Vector3.Zero, Orientation.Identity) { SideId = mission.Sides[1].Id };
                mission.PlaceShip(bs, s0);
                placed++;
                return (bs, s0);
            });

            server.Boot(Config("P542 Remote Projectiles"));
            foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass ready-up
            Console.SetOut(tee);
            Console.WriteLine($"P542-PROJ: booted XXV_simple :{server.BoundPort} window={windowSeconds:F0}s");

            using var alpha = Connect(server, "AlphaPlayer", 94000000000000001ul);
            using var bravo = Connect(server, "BravoShooter", 94000000000000002ul);
            int seated = PumpUntil(server, () => alpha.World.MyShipId is { } && bravo.World.MyShipId is { }, 4000);
            Assert.True(seated < 4000, "both peers seated");
            short alphaId = alpha.World.MyShipId!.Value;
            short bravoId = bravo.World.MyShipId!.Value;

            var bravoServer = ServerShip(server, bravoId)!;
            var alphaServer = ServerShip(server, alphaId)!;
            var mission = bravoServer.Mission ?? throw new InvalidOperationException("bravo mission");
            var weapon = RealWeapon(mission.Core);
            short typeId = weapon.ProjectileTypeId;

            // Arm BOTH with the SAME weapon, both auto-firing at each other. The
            // local player (Alpha) firing too is what makes its own-shot tracer
            // live — modelled here by the always-true drop predicate (a same-type
            // local round recently emitted, exactly the P462 precondition).
            ArmShooter(bravoServer, alphaServer, weapon);
            ArmShooter(alphaServer, bravoServer, weapon);
            alpha.World.OwnEchoDropPredicate = (t, sp, pos, now) => true;   // player actively firing same gun

            // Alpha sees the cross-side Bravo ship before the fire storm (so the
            // rounds classify as Enemy, not unknown).
            int learned = PumpUntil(server, () =>
            {
                lock (alpha.Gate)
                    return alpha.World.Ships.TryGetValue(bravoId, out var e) && e.Latest is not null && !e.IsNotVisible;
            }, 3000);

            var t0 = DateTime.UtcNow;
            var deadline = t0.AddSeconds(windowSeconds);
            var camEye = new Vector3(0f, 0f, 12f);   // cockpit-ish eye 12 u ahead of the local ship
            while (DateTime.UtcNow < deadline)
            {
                server.TickSim();
                // Keep both fighters alive for the whole window so the fight (and
                // cross-side delivery) sustains — otherwise the point-blank
                // mutual fire murders both and the audiences evaporate.
                lock (server)
                {
                    alphaServer.SetFraction(1.0f); alphaServer.SetShieldFraction(1.0f);
                    bravoServer.SetFraction(1.0f); bravoServer.SetShieldFraction(1.0f);
                }
                // Drive the draw decision every tick so Drawn* accumulates over
                // the window exactly as the MonoGame client does per frame.
                lock (alpha.Gate)
                {
                    var w = alpha.World;
                    if (w.MyShipId is { } me && w.Ships.TryGetValue(me, out var e) && e.Latest is { } up)
                        w.DiagnoseDraw(up.Position, camEye, Environment.TickCount64, null);
                }
                Thread.Sleep(2);
            }

            // Headline counts after the fight window (cumulative).
            var snap = WorldSnap.Of(alpha);
            int serverShots = ServerShotsToward(tee.Captured, bravoId);

            // Server-side ship states after the window (did anyone die / leave?).
            var alphaSrv = ServerShip(server, alphaId);
            var bravoSrv = ServerShip(server, bravoId);
            Console.WriteLine($"P542-PROJ post alpha id={alphaId} hp={alphaSrv?.HitPoints} sector={(alphaSrv?.Sector?.Id)} alive={alphaSrv?.Sector is not null}");
            Console.WriteLine($"P542-PROJ post bravo id={bravoId} hp={bravoSrv?.HitPoints} sector={(bravoSrv?.Sector?.Id)} alive={bravoSrv?.Sector is not null}");

            Console.WriteLine($"P542-PROJ type={typeId} alpha={alphaId} bravo={bravoId}");
            Console.WriteLine($"P542-PROJ server [projectile] lines naming Bravo's fire: {serverShots}");
            Console.WriteLine($"P542-PROJ client-diag {snap.Diag.FormatLine()}");

            // Dump BEFORE assertions so a buggy (pre-fix) run still records the
            // measured counts for the before/after comparison.
            var diag = new StringBuilder();
            diag.AppendLine($"--- P542 DIAG type={typeId} ---");
            diag.AppendLine($"serverShots(bravo)={serverShots} receivedEnemy={snap.Diag.ReceivedEnemy} droppedEnemy={snap.Diag.DroppedEnemy} drawnEnemy={snap.Diag.DrawnEnemy}");
            diag.AppendLine($"post alpha id={alphaId} hp={alphaSrv?.HitPoints} sector={(alphaSrv?.Sector?.Id)} alive={alphaSrv?.Sector is not null}");
            diag.AppendLine($"post bravo id={bravoId} hp={bravoSrv?.HitPoints} sector={(bravoSrv?.Sector?.Id)} alive={bravoSrv?.Sector is not null}");
            diag.AppendLine($"diagLine: {snap.Diag.FormatLine()}");
            diag.AppendLine("\n--- SERVER LOG (projectile lines) ---");
            foreach (var line in tee.Captured.Split('\n'))
                if (line.Contains("[projectile]", StringComparison.Ordinal) || line.Contains("[autoreload]", StringComparison.Ordinal))
                    diag.AppendLine(line);
            var scratch = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp", "p542_diag.txt");
            File.WriteAllText(scratch, diag.ToString());
            Console.WriteLine($"P542-PROJ diag dumped to {scratch}");

            // The enemy's shots must SURVIVE the own-echo de-dup (the fix).
            Assert.Equal(0, snap.Diag.DroppedEnemy);
            Assert.True(snap.Diag.ReceivedEnemy > 0, "client must receive the enemy shooter's PROJECTILE_INSTANCE broadcasts");
            Assert.True(snap.Diag.DrawnEnemy > 0, "client must DRAW the enemy's rounds (were invisible before the fix)");

            return 1;
        }
        finally
        {
            Console.SetOut(saved);
        }
    }

    private static void ArmShooter(Ship shooter, Ship target, DataPartTypeIGC.Weapon weapon)
    {
        shooter.StateM |= ShipControlState.AllWeapons;
        shooter.Energy = 1_000_000f;
        shooter.Ammo = 30_000;
        shooter.MountWeapon(weapon);
        shooter.CommandTarget = target;
        shooter.Orientation.Set((target.Position - shooter.Position).Normalize());
    }

    private static Ship? ServerShip(Srv server, short id)
    {
        foreach (var m in server.Missions)
            foreach (var sector in m.Sectors)
                foreach (var model in sector.Models)
                    if (model is Ship s && s.Id == id)
                        return s;
        return null;
    }

    /// <summary>The live proof at the default 20 Hz server cadence — an enemy
    /// shooter fires the SAME projectile type as the (actively firing) local
    /// player at ≤150 u; the client must receive and DRAW those rounds rather
    /// than swallowing them as its own echo.</summary>
    [Fact]
    public void EnemyShots_OfSameTypeAtCloseRange_AreReceivedAndDrawn() => Run(windowSeconds: 8);
}
