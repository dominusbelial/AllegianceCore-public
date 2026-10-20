using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Audio;
using SimVec = Allegiance.Core.Collision.Vector3;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using BotClass = global::Allegiance.Bot.Bot;

// P463 live-proof harness (HEADLESS — no graphics device). Boots a REAL
// in-process server hosting a REAL fight and connects REAL bots + a REAL
// ConnectionSession observer, then exercises the full P463 pipeline over the
// REAL wire:
//
//   MODE other  — an armed shooter (side 0) pours a cheap, fully-shield-absorbed
//                 round into a shielded enemy target (side 1); a same-side
//                 observer CLIENT receives the PROJECTILE_HULL_HIT(213) with
//                 absorbedByShield=true  → classifies OTHER_SHIELD. The target's
//                 shield is then knocked down so subsequent rounds leak to hull
//                 → absorbedByShield=false → OTHER_HULL.
//   MODE my     — an armed enemy shooter (side 1) pours the same round into the
//                 OBSERVER'S OWN ship (side 0, shielded): the observer is a
//                 cross-side viewer of the shot, so it receives its own hits
//                 with TargetId == MyShipId → MY_SHIELD (shield absorbing) then
//                 MY_HULL (shield knocked down).
//
// Every classification is the REAL HitSoundSelector output on the REAL drained
// HitSoundEvent stream, printed to the console exactly like the client's
// "[monogame] audio: hit sound <Kind>" line. The server's own
// "[projectile-hit] … absorbedByShield=…" broadcast lines are surfaced too, so
// the shield flag is visible end-to-end.

int rc = Proof.Run(args);
return rc;

static class Proof
{
    static string ArtRoot()
    {
        var env = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH");
        if (env is { Length: > 0 }) return env;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Artwork", "sounddef.mdl"))) return Path.Combine(dir.FullName, "Artwork");
            dir = dir.Parent;
        }
        return ExternalPaths.Resolve(ExternalResourceKind.Artwork);
    }

    static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot(), "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    // The cheapest dispersion-0 damaging gun (many rounds, sustained fire) …
    static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon)> Fight = new(() =>
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
        throw new InvalidOperationException("no fightable hull+weapon");
    });

    // A SHIELD part whose damage cell vs the cheap round is > 0 and which FULLY
    // absorbs a single hit (≤ 0.5 ≪ the 0.9 useful ceiling ⇒ dr == ShieldDamage).
    static readonly Lazy<DataPartTypeIGC> AbsorbShield = new(() =>
    {
        var core = RepresentativeCore();
        var (_, weapon) = Fight.Value;
        var pt = core.ProjectileTypes[weapon.ProjectileTypeId];
        foreach (var sp in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Shield))
        {
            var st = (DataPartTypeIGC.Shield)sp.Type;
            if (st.MaxStrength <= 0f) continue;
            float cell = core.Constants.DamageConstants[pt.DamageType][st.DefenseType];
            if (cell <= 0f) continue;
            float absorbed = pt.Power * cell / st.MaxStrength;
            if (absorbed >= 0.2f && absorbed <= 0.5f) return sp;
        }
        throw new InvalidOperationException("no fully-absorbed shield part for the cheap round");
    });

    // A high-HP hull the cheap round actually damages, so the fight lands many
    // hits before the victim dies.
    static readonly Lazy<DataHullTypeIGC> SurvivorHull = new(() =>
    {
        var core = RepresentativeCore();
        var pt = core.ProjectileTypes[Fight.Value.weapon.ProjectileTypeId];
        DataHullTypeIGC? best = null; float bestHp = -1f;
        foreach (var h in core.HullTypes.Values)
        {
            float cell = core.Constants.DamageConstants[pt.DamageType][h.DefenseType];
            if (cell > 0f && h.HitPoints > bestHp) { best = h; bestHp = h.HitPoints; }
        }
        if (best is null) throw new InvalidOperationException("no hull the cheap round damages");
        return best.Value;
    });

    static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot(),
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: proof observer sessions never send POSITIONREQ
        Games =
        [
            new Gm { Name = "P463 Hit-Sound Proof", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 463u, DwCookie = 0x463u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 97800000000000000ul, DrmHash = "hitsoundproof",
    };

    static void PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        if (n >= max) Console.WriteLine("[hitsound] pump TIMEOUT");
    }

    static IEnumerable<Ship> AllShips(Srv server) =>
        server.Missions.SelectMany(m => m.Sectors).SelectMany(s => s.Models).OfType<Ship>();

    static void KnockShield(Ship s)
    {
        var f = typeof(Ship).GetField("_shieldFraction", BindingFlags.Instance | BindingFlags.NonPublic)!;
        f.SetValue(s, 0f);   // ≤ 0.1 "down" ⇒ subsequent rounds leak to hull (shipIGC.cpp:1287-1291)
    }

    static void BootAndConnect(Srv server, out BotClass shooter, out BotClass? targetBot, out ConnectionSession viewer, bool withVictim)
    {
        Console.WriteLine("[hitsound] booting in-process server (m2xspiderw.igc)...");
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass ready-up lobby
        ushort bound = server.BoundPort;
        Console.WriteLine($"[hitsound] server transport listening on 127.0.0.1:{bound}");

        shooter = new BotClass();
        shooter.Connect(bound, "PSHOOT");
        shooter.SendLogonRequest(Request("PROOFSHOOTER"));

        targetBot = null;
        if (withVictim)   // the enemy victim's driver (mode other only) seats at placed 1
        {
            targetBot = new BotClass();
            targetBot.Connect(bound, "PTGT");
            targetBot.SendLogonRequest(Request("PROOFTARGET"));
        }

        viewer = new ConnectionSession();
        viewer.Connect(bound, "proof_viewer");
        viewer.SendLogonRequest(Request("proof_viewer"));
    }

    // NOTE: caller sets the mode-specific spawner first (InjectOther / InjectMy).
    public static int Run(string[] args)
    {
        string mode = args.FirstOrDefault() ?? "other";
        bool ok = mode == "my" ? RunMy() : RunOther();
        Console.WriteLine(ok ? "[hitsound] hitsound-proof PASS (live wire + classification)"
                             : "[hitsound] hitsound-proof FAILED");
        return ok ? 0 : 1;
    }

    // ── MODE other ─────────────────────────────────────────────────────────

    static void InjectOther(Srv server)
    {
        const float TargetZ = -180f;
        var placed = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var sector = mission.Sectors[0];
            var (hull, weapon) = Fight.Value;
            if (placed == 0)
            {
                var s = new Ship(id, hull, new SimVec(0f, 0f, 0f), SimVec.Zero, Orientation.Identity);
                s.SideId = 0; s.Energy = hull.MaxEnergy * 100f;
                mission.PlaceShip(s, sector); s.MountWeapon(weapon); placed++;
                return (s, sector);
            }
            if (placed == 1)   // shielded ENEMY target (victim of the shooter)
            {
                var thull = SurvivorHull.Value;
                var s = new Ship(id, thull, new SimVec(0f, 0f, TargetZ), SimVec.Zero, Orientation.Identity);
                s.SideId = 1;
                mission.PlaceShip(s, sector);
                s.MountPart(AbsorbShield.Value, amount: 1, mountId: 0, fractionMounted: 255);   // fully-absorbing shield
                placed++;
                return (s, sector);
            }
            // the same-side observer viewer
            var v = new Ship(id, hull, new SimVec(-420f, 0f, 60f), SimVec.Zero, Orientation.Identity);
            v.SideId = 0; mission.PlaceShip(v, sector); placed++;
            return (v, sector);
        };
    }

    static bool RunOther()
    {
        using var server = new Srv();
        InjectOther(server);
        BootAndConnect(server, out var shooter, out var targetBot, out var viewer, withVictim: true);
        PumpUntil(server, () => viewer.World.MyShipId is not null
                                && shooter.ShipAssignment is not null
                                && targetBot!.ShipAssignment is not null, 6000);

        var target = AllShips(server).First(s => s.SideId == 1);
        short targetId = target.Id;
        short myId = viewer.World.MyShipId!.Value;
        for (int t = 0; t < 300; t++) server.TickSim();   // let same-side exports settle
        Console.WriteLine($"[hitsound] MODE other: viewer MyShipId={myId}, shielded enemy target ship {targetId} " +
                          $"'{target.HullType.Name}' shield-fraction {target.ShieldFraction:0.###}");

        shooter.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);
        Console.WriteLine("[hitsound] shooter opened fire on the shielded target — collecting OTHER_SHIELD hits...");

        int shieldHits = 0, hullHits = 0;
        var bad = new List<string>();
        int guard = 0;
        while (guard < 9000)
        {
            server.TickSim(); Thread.Sleep(2);
            lock (viewer.Gate)
            {
                foreach (var ev in viewer.World.TakeHitSoundEvents())
                {
                    if (ev.ClusterId != target.Sector!.Id) { bad.Add("wrong sector"); continue; }
                    foreach (var (tid, kind, _) in HitSoundSelector.Select(new[] { ev }, viewer.World.MyShipId))
                    {
                        if (tid != targetId) { bad.Add($"classified target {tid} != {targetId}"); continue; }
                        if (ev.AbsorbedByShield)
                        {
                            if (kind != HitSoundKind.OtherShield) bad.Add($"expected OTHER_SHIELD got {kind}");
                            shieldHits++;
                        }
                        else
                        {
                            if (kind != HitSoundKind.OtherHull) bad.Add($"expected OTHER_HULL got {kind}");
                            hullHits++;
                        }
                    }
                }
            }
            if (shieldHits >= 3 && hullHits >= 2) break;    // harvested both cases
            if (shieldHits >= 3 && hullHits == 0 && target.ShieldFraction < 0.2f)
            {
                // Shield drained toward down but no hull hits yet — force it down so
                // the transition to OTHER_HULL is clean.
                KnockShield(target);
                Console.WriteLine($"[hitsound] knocked the target's shield to 0 — subsequent rounds leak to hull (OTHER_HULL)");
            }
            guard++;
        }
        Console.WriteLine($"[hitsound] MODE other: drained {shieldHits} OTHER_SHIELD + {hullHits} OTHER_HULL hits "
                          + (bad.Count == 0 ? "(all correct) " : $"(BAD: {string.Join("; ", bad)} )")
                          + $"guard={guard}");
        if (bad.Count == 0 && shieldHits >= 3 && hullHits >= 2) return true;
        Console.Error.WriteLine("hitsound-proof FAILED (other mode): wrong classification or insufficient hits.");
        return false;
    }

    // ── MODE my ────────────────────────────────────────────────────────────

    static void InjectMy(Srv server)
    {
        const float VictimZ = -180f;
        var placed = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var sector = mission.Sectors[0];
            var (hull, weapon) = Fight.Value;
            if (placed == 0)   // ENEMY shooter firing at the viewer
            {
                var s = new Ship(id, hull, new SimVec(0f, 0f, 0f), SimVec.Zero, Orientation.Identity);
                s.SideId = 1; s.Energy = hull.MaxEnergy * 100f;
                mission.PlaceShip(s, sector); s.MountWeapon(weapon); placed++;
                return (s, sector);
            }
            // the observer viewer is the VICTIM (side 0, shielded) — its own shield absorbs the enemy's rounds
            var v = new Ship(id, SurvivorHull.Value, new SimVec(0f, 0f, VictimZ), SimVec.Zero, Orientation.Identity);
            v.SideId = 0;
            mission.PlaceShip(v, sector);
            v.MountPart(AbsorbShield.Value, amount: 1, mountId: 0, fractionMounted: 255);
            placed++;
            return (v, sector);
        };
    }

    static bool RunMy()
    {
        using var server = new Srv();
        InjectMy(server);
        BootAndConnect(server, out var shooter, out _, out var viewer, withVictim: false);
        PumpUntil(server, () => viewer.World.MyShipId is not null && shooter.ShipAssignment is not null, 6000);

        short myId = viewer.World.MyShipId!.Value;
        var me = AllShips(server).First(s => s.Id == myId);
        for (int t = 0; t < 300; t++) server.TickSim();
        Console.WriteLine($"[hitsound] MODE my: viewer MyShipId={myId} '{me.HullType.Name}', enemy shooter {AllShips(server).First(s => s.SideId == 1).Id} " +
                          $"aimed at it; own shield-fraction {me.ShieldFraction:0.###}");

        shooter.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);
        Console.WriteLine("[hitsound] enemy opened fire on OUR ship — collecting MY_SHIELD hits...");

        int shieldHits = 0, hullHits = 0;
        var bad = new List<string>();
        int guard = 0;
        while (guard < 9000)
        {
            server.TickSim(); Thread.Sleep(2);
            lock (viewer.Gate)
            {
                foreach (var ev in viewer.World.TakeHitSoundEvents())
                {
                    foreach (var (tid, kind, _) in HitSoundSelector.Select(new[] { ev }, viewer.World.MyShipId))
                    {
                        if (tid != myId) { bad.Add($"my-mode target {tid} != our ship {myId}"); continue; }
                        if (ev.AbsorbedByShield)
                        {
                            if (kind != HitSoundKind.MyShield) bad.Add($"expected MY_SHIELD got {kind}");
                            shieldHits++;
                        }
                        else
                        {
                            if (kind != HitSoundKind.MyHull) bad.Add($"expected MY_HULL got {kind}");
                            hullHits++;
                        }
                    }
                }
            }
            if (shieldHits >= 3 && hullHits >= 2) break;
            if (shieldHits >= 3 && hullHits == 0)
            {
                KnockShield(me);
                Console.WriteLine($"[hitsound] knocked OUR shield to 0 — subsequent rounds leak to hull (MY_HULL)");
            }
            guard++;
        }
        Console.WriteLine($"[hitsound] MODE my: drained {shieldHits} MY_SHIELD + {hullHits} MY_HULL hits "
                          + (bad.Count == 0 ? "(all correct)" : $"(BAD: {string.Join("; ", bad)})"));
        if (bad.Count == 0 && shieldHits >= 3 && hullHits >= 2) return true;
        Console.Error.WriteLine("hitsound-proof FAILED (my mode): wrong classification or insufficient hits.");
        return false;
    }
}
