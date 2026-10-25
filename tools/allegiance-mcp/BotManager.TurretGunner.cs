using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P814 — the bot-side turret gunner slice of <see cref="BotManager"/> (partial
/// class, shares the private <c>BotManager</c> members). Implements the three
/// gunner behaviours against the PORT's existing native-faithful boarding +
/// turret-gunner machinery (P108/P109/P464/P803 — the same server/client code
/// human gunners use):
/// <list type="number">
/// <item><b>Boarding</b> — a docked, eligible bot prefers to board a teammate's
/// docked turret carrier with a free, equipped seat instead of launching its own
/// ship (item 4a, native <c>BoardShip</c>, FedSrv.CPP:2322-2475; the free-seat
/// count is the native <c>MaxWeapons − MaxFixedWeapons</c> measure the client
/// already uses, teleportpane.cpp:520-531). On <c>BOARD_NACK</c> or timeout it
/// falls back to launching normally (item 2).</item>
/// <item><b>Carrier gather</b> — a bot pilot docked in a turret hull holds its
/// launch for a bounded gather window so docked teammates can board (item 4a),
/// then undocks via <c>FM_C_DOCKED(48)</c> (<c>SendDocked</c>, the real undock
/// announce, Messages.h:402-404 / Server.Docking.cs:2127).</item>
/// <item><b>Gunner AI</b> — while boarded, drive the turret through the native
/// gunner path: aim via the shared Core <see cref="ShipDodgeLaw.SolveForImpact"/>
/// (native <c>solveForLead</c>, shipIGC.cpp:2463-2468), slew the turret at the
/// native <c>ExecuteTurretMove</c> rates (shipIGC.cpp:1640-1641), and emit the
/// <c>ACTIVE/INACTIVE_TURRET_UPDATE(99/100)</c> pair at the control cadence —
/// the exact same wire the human <c>TurretGunner.cs</c> uses. The server sets
/// <c>weaponsMaskIGC</c> on a valid ACTIVE (shipIGC.cpp:3358-3359), so a valid
/// ACTIVE = firing.</item>
/// </list>
/// Behaviour parity is the contract; visual presentation is irrelevant here
/// (no renderer). All decisions funnel through <see cref="GunnerPolicy"/>.
/// </summary>
internal sealed partial class BotManager
{
    /// <summary>Native control cadence (<c>c_dsecUpdateClient</c> = 0.15 s) —
    /// the interval between ACTIVE/INACTIVE turret sends, matching the human
    /// gunner cadence and the bot's own flight control cadence.</summary>
    private const float TurretControlDt = 0.15f;

    private static GunnerPolicy.Tuning GunnerTune => GunnerPolicy.Effective;

    private static Showcase.HullRole RoleOf(ActiveBot bot)
        => Enum.TryParse<Showcase.HullRole>(bot.Role, true, out var r) ? r : Showcase.HullRole.Fighter;

    /// <summary>Whether this bot is currently a boarded TURRET gunner (its own
    /// ship is parented to a carrier and crews a real turret slot — not an
    /// observer, <c>BoardTurretObserver</c> = native NA).</summary>
    private static bool IsTurretGunner(ActiveBot bot, ConnectionSession cs, out short carrierId, out short mount)
    {
        carrierId = -1; mount = -1;
        if (cs.World.MyShipId is not { } my || !cs.World.Ships.TryGetValue(my, out var me)) return false;
        if (me.ParentId < 0) return false;
        if (me.BoardTurretId < 0 || me.BoardTurretId == ShipEntity.BoardTurretObserver) return false;
        carrierId = me.ParentId;
        mount = me.BoardTurretId;
        return true;
    }

    /// <summary>
    /// P814 — the gunner flow gate, called from <see cref="DriveCombat"/> before
    /// the normal launch path. Returns true when the gunner subsystem owns this
    /// bot's tick (it is driving a turret, gathering gunners, or waiting on a
    /// board); false lets the normal launch/flight proceed.
    /// </summary>
    private bool TryGunnerFlow(ActiveBot bot, double now)
    {
        if (!GunnerTune.Enable) return false;
        var cs = bot.Cs;
        if (cs is null) return false;
        if (cs.World.MyShipId is not { } my) return false;

        bool boarded; short parent, mount, station;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(my, out var me)) return false;
            boarded = me.ParentId >= 0;
            parent = me.ParentId;
            mount = me.BoardTurretId;
            station = me.StationId;
        }

        // 1 — already a boarded turret gunner → drive the turret, nothing else.
        if (boarded)
        {
            if (mount >= 0 && mount != ShipEntity.BoardTurretObserver)
            {
                DriveTurretGunner(bot, cs, now, parent, mount);
                return true;
            }
            return false;   // observer (no turret slot) — normal life resumes
        }

        // 2 — docked: a carrier holds its launch for gunners; an eligible fighter
        // prefers to board a teammate carrier instead of launching. Not before the
        // side has found an enemy station (P745: fighters held docked or riding as
        // gunners stalled exploration, so a side never discovered the enemy base),
        // and never more than a fifth of the side in turret seats.
        // A gather or board attempt already under way always finishes (a carrier that
        // bought its hull must still launch); the gates only stop NEW ones.
        bool inProgress = bot.CarrierHullBought || bot.GunnerCarrierId is not null;
        if (station >= 0 && (inProgress || (SideKnowsEnemyStation(bot, cs) && !SideGunnerCapReached(bot, cs))))
        {
            if (ManageCarrierGather(bot, cs, now)) return true;
            if (TrySeekBoard(bot, cs, now)) return true;
        }
        return false;
    }

    private static bool SideKnowsEnemyStation(ActiveBot bot, ConnectionSession cs)
    {
        lock (cs.Gate)
            foreach (var st in cs.World.Stations.Values)
                if (st.SideId >= 0 && st.SideId != bot.Side) return true;
        return false;
    }

    /// <summary>At most a fifth of the side's ships ride in turret seats (at least one).</summary>
    private static bool SideGunnerCapReached(ActiveBot bot, ConnectionSession cs)
    {
        int sideShips = 0, gunners = 0;
        lock (cs.Gate)
            foreach (var ship in cs.World.Ships.Values)
            {
                if (ship.SideId != bot.Side) continue;
                sideShips++;
                if (ship.ParentId >= 0) gunners++;
            }
        return gunners >= Math.Max(1, sideShips / 5);
    }

    // ── 1. gunner AI: aim + fire through the native turret path ───────────

    private void DriveTurretGunner(ActiveBot bot, ConnectionSession cs, double now,
        short carrierId, short turretMount)
    {
        // The turret-facing integration needs to be continuous with the server's
        // authoritative ExecuteTurretMove, so seed once per gunning episode from
        // this gunner's own last-known pose (same choice as the human client,
        // Game1.TurretGunner.TurretSeedOrientation).
        if (!bot.TurretSeeded)
        {
            lock (cs.Gate)
                if (cs.World.Ships.TryGetValue(bot.ShipId, out var own) && own.Latest is { } up)
                    bot.TurretOrientation = up.Orientation.Copy();
            bot.TurretSeeded = true;
        }
        bot.GunnerCarrierId = carrierId;
        bot.TurretMount = turretMount;
        if (bot.GunnerBoardings == 0)
        {
            bot.GunnerBoardings++;
            Console.WriteLine($"[gunner] board ok: {bot.Name} (ship {bot.ShipId}) -> carrier {carrierId} turret mount {turretMount}");
        }

        var (weaponRange, projSpeed) = ResolveTurretBallistics(bot, cs, carrierId, turretMount);

        // Pick a target: the nearest enemy threatening the carrier, fighters
        // first, then an inbound hostile missile (native projectiles collide
        // with and destroy missiles — turrets legitimately shoot them down).
        if (!TryPickGunnerTarget(cs, carrierId, bot.Side, out var targetPos, out var targetVel,
                out short targetId, out float dist) )
        {
            cs.SendInactiveTurret();    // no target → stop firing, keep tracking neutral
            return;
        }

        Vector3 carrierPos, carrierVel;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(carrierId, out var carrier) || carrier.Latest is not { } cpose)
            { cs.SendInactiveTurret(); return; }
            carrierPos = cpose.Position;
            carrierVel = cpose.Velocity;
        }

        // Lead with the shared Core solver; fall back to the direct bearing when
        // no intercept exists (target receding).
        Vector3 aim = targetPos - carrierPos;
        bool lead = GunnerPolicy.TryLead(carrierPos, carrierVel, targetPos, targetVel, projSpeed, out var leadDir, out _);
        if (lead && leadDir.LengthSquared() > 1e-6f) aim = leadDir;
        else aim = aim.LengthSquared() > 1e-6f ? aim.Normalize() : bot.TurretOrientation.GetForward();

        float remaining = GunnerPolicy.SlewToward(bot.TurretOrientation, aim, TurretControlDt, GunnerTune);
        var controls = GunnerPolicy.AimControls(bot.TurretOrientation, aim);

        uint serverMs = GunnerServerTimeMs(bot, cs);
        bool fire = GunnerPolicy.FireGate(remaining, dist, weaponRange, GunnerTune) && serverMs != 0u;
        if (fire)
        {
            cs.SendActiveTurret(serverMs, ClientActiveTurretUpdate.From(serverMs, controls, bot.TurretOrientation));
            bot.GunnerShots++;
            SampleGunnerTargetHealth(bot, cs, targetId);   // hit/kill bookkeeping
            if (bot.GunnerShots % 15 == 0)
                Console.WriteLine($"[gunner] {bot.Name} firing turret {turretMount} at {targetId} dist={dist:F0} remaining={remaining * 57.3f:F1}deg");
        }
        else
        {
            cs.SendInactiveTurret();
        }
    }

    /// <summary>Feed the bot's server-clock from the PING echoes and return the
    /// estimated server sim-ms to stamp ACTIVE_TURRET_UPDATE (within the
    /// server's ±1.5 s window, shipIGC.cpp:3353-3354).</summary>
    private static uint GunnerServerTimeMs(ActiveBot bot, ConnectionSession cs)
    {
        foreach (var (serverMs, localMs) in cs.TakePingEchoes())
            bot.ServerClock.AddSample(serverMs, localMs);
        bot.ServerClock.SetTimeScale(cs.World.TimeScale, Environment.TickCount64);
        return bot.ServerClock.ServerTimeMs(Environment.TickCount64);
    }

    /// <summary>The ballistics of the weapon the gunner crews — the carrier's
    /// mounted turret weapon at this gunner's slot (from the same-side
    /// LOADOUT_CHANGE the port broadcasts), else this bot's own lead weapon's
    /// projectile. Zero speed → direct aim + no range gate.</summary>
    private static (float Range, float Speed) ResolveTurretBallistics(ActiveBot bot,
        ConnectionSession cs, short carrierId, short turretMount)
    {
        var core = Catalog.RepresentativeCore();
        lock (cs.Gate)
        {
            if (cs.World.Ships.TryGetValue(carrierId, out var carrier) && carrier.Loadout is { } ld)
            {
                foreach (var p in ld)
                {
                    if (p.MountId != turretMount || p.Amount <= 0) continue;
                    if (core.PartTypes.TryGetValue(p.PartId, out var part)
                        && part.Type is DataPartTypeIGC.Weapon w
                        && core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var r)
                        && r.Speed > 0 && r.Lifespan > 0)
                        return (r.Speed * r.Lifespan, r.Speed);
                }
            }
        }
        if (bot.WeaponPartId is { } wp && core.PartTypes.TryGetValue(wp, out var own)
            && own.Type is DataPartTypeIGC.Weapon w2
            && core.ProjectileTypes.TryGetValue(w2.ProjectileTypeId, out var r2) && r2.Speed > 0)
            return (r2.Speed * r2.Lifespan, r2.Speed);
        return (0f, 0f);
    }

    /// <summary>Pick the gunner's target: fighters/attack craft first (the
    /// "fighters first" order), preferring enemies actually attacking the
    /// carrier, then an inbound hostile missile; else the nearest visible enemy
    /// ship in the carrier's sector. (Native turret projectiles hit and destroy
    /// missiles, so engaging them is legitimate.)</summary>
    private static bool TryPickGunnerTarget(ConnectionSession cs, short carrierId, short mySide,
        out Vector3 targetPos, out Vector3 targetVel, out short targetId, out float dist)
    {
        targetPos = default; targetVel = default; targetId = -1; dist = float.MaxValue;
        short sector;
        Vector3 carrierPos;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(carrierId, out var carrier) || carrier.Latest is not { } cp) return false;
            sector = carrier.SectorId;
            carrierPos = cp.Position;

            ShipEntity? best = null; float bestD = float.MaxValue;
            foreach (var ship in cs.World.Ships.Values)
            {
                if (ship.IsNotVisible || ship.SideId == mySide) continue;
                if (ship.SectorId != sector) continue;
                if (ship.Latest is not { } lu) continue;
                if (CombatTargetSelector.ClassifyTargetId(ship.HullId) == BotTargetClass.MinerDrone) continue;
                float d = (lu.Position - carrierPos).Length();
                if (d < bestD) { bestD = d; best = ship; }
            }
            if (best is { } picked && picked.Latest is { } pl)
            {
                targetPos = pl.Position; targetVel = pl.Velocity;
                targetId = picked.ShipId; dist = bestD;
                return true;
            }

            // No eligible enemy ship: engage an inbound hostile missile near the carrier.
            foreach (var m in cs.World.Missiles.Values)
            {
                if (m.ClusterId != sector) continue;
                float d = (m.Position - carrierPos).Length();
                if (d < bestD) { bestD = d; targetPos = m.Position; targetVel = Vector3.Zero; targetId = m.MissileId; }
            }
            if (targetId >= 0) { dist = bestD; return true; }
        }
        return false;
    }

    /// <summary>Best-effort hit/kill bookkeeping: while the gunner is firing at
    /// <paramref name="targetId"/>, a hull-fraction drop marks a hit and a dead
    /// target a kill. Approximate (the carrier owns the credited shooter), kept
    /// bounded — the authoritative per-shooter hit stream stays in the carrier's
    /// own SampleCombatStats.</summary>
    private static void SampleGunnerTargetHealth(ActiveBot bot, ConnectionSession cs, short targetId)
    {
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(targetId, out var t)) return;
            if (t.IsNotVisible)
            {
                if (bot.GunnerKills == 0 || bot.LastGunnerKill != targetId) { bot.GunnerKills++; bot.LastGunnerKill = targetId; }
                return;
            }
            if (t.Latest is { } lu && lu.HullFraction < bot.GunnerLastHull - 0.002f)
            {
                bot.GunnerHits++;
                bot.GunnerLastHull = lu.HullFraction;
            }
            else if (t.Latest is { } lu2)
            {
                bot.GunnerLastHull = lu2.HullFraction;
            }
        }
    }

    // ── 2. carrier gather: hold the launch so gunners can board ───────────

    /// <summary>Item 4a — while a bot pilot is docked in a turret-capable carrier
    /// hull, hold the launch for the bounded gather window (cut short when the
    /// seats fill or the pilot is needed urgently), then undock. Returns true
    /// while the carrier's launch is being managed here.</summary>
    private bool ManageCarrierGather(ActiveBot bot, ConnectionSession cs, double now)
    {
        if (!GunnerPolicy.CanHost(RoleOf(bot))) return false;
        // A finale bomber never waits for gunners: the finale's massing is the
        // single-player payoff (P745: docked holds left the strike one bomber short at
        // the staging timeout). An already-bought gather still launches below.
        if (!bot.CarrierHullBought && (bot.FinaleBomberReserved || bot.FinaleBomberActive)) return false;
        var core = Catalog.RepresentativeCore();
        // The hull this bot is ABOUT TO LAUNCH in (bot.HullId, what the docked pre-buy
        // below seats it in) — not its live hull: a bot docked after dying is still a
        // lifepod, which has no turret seats, so no carrier ever qualified (Claude live
        // test: 0 boardings in a 15-minute showcase).
        if (!core.HullTypes.TryGetValue(bot.HullId, out var hull)) return false;
        int capacity = GunnerPolicy.TurretCapacity(hull);
        if (capacity <= 0) return false;             // not a turret carrier

        if (!bot.CarrierHullBought)
        {
            // Seat docked in the turret hull WITHOUT launching so gunners can
            // board it while both are docked (native BoardShip precondition,
            // FedSrv.CPP:2342-2345). Scope 1 ensured the turret mounts are armed.
            cs.SendBuyLoadout(bot.HullId, fLaunch: false, bot.Parts ?? []);
            bot.CarrierHullBought = true;
            bot.CarrierGatherUntil = now + GunnerTune.GatherSeconds;
            short stat;
            lock (cs.Gate) stat = cs.World.Ships.TryGetValue(bot.ShipId, out var sm) ? sm.StationId : (short)-1;
            Console.WriteLine($"[gunner] carrier {bot.Name} (ship {bot.ShipId}) docked in turret hull at station {stat}, holding launch up to {GunnerTune.GatherSeconds:F0}s for gunners");
            return true;
        }

        int gunners = CountCarrierGunners(cs, bot.ShipId);
        bool urgent = CarrierUrgentLaunch(bot, cs, now);
        int recruit = GunnerPolicy.RecruitLimit(capacity, GunnerTune);
        if (urgent || now >= (bot.CarrierGatherUntil ?? double.PositiveInfinity) || gunners >= recruit)
        {
            cs.SendDocked();                             // FM_C_DOCKED(48) undock announce
            bot.CarrierHullBought = false;
            bot.CarrierGatherUntil = null;
            bot.GunnerCarrierId = null;
            bot.MissionLaunchPending = false;            // this carrier life is launched
            bot.Launched = true;
            bot.GunnerKillsAtLaunch = gunners;
            Console.WriteLine($"[gunner] carrier {bot.Name} launching with {gunners}/{recruit} gunner(s){(urgent ? " (urgent)" : "")}");
            return true;
        }
        return true;                                     // still gathering — owned
    }

    /// <summary>Item 2 — count this carrier's current turret gunners: its live
    /// children in the TURRET state (the same free-seat definition the client
    /// already uses, ClientWorld.Apply(BoardNack)).</summary>
    private static int CountCarrierGunners(ConnectionSession cs, short carrierId)
    {
        int n = 0;
        lock (cs.Gate)
            foreach (var s in cs.World.Ships.Values)
                if (s.ParentId == carrierId && s.State == ShipStatusMessage.StateTurret) n++;
        return n;
    }

    /// <summary>Cut the gather short when the pilot is needed urgently: a finale
    /// go time (P801 release) or a friendly station under attack in the carrier's
    /// sector.</summary>
    private static bool CarrierUrgentLaunch(ActiveBot bot, ConnectionSession cs, double now)
    {
        if (bot.FinaleBomberActive && bot.FinaleReleaseAt is { } go && now >= go) return true;
        lock (cs.Gate)
        {
            foreach (var kv in bot.RecentStationAttackers)
                if (now - kv.Value < ThreatWindowSeconds) return true;
        }
        return false;
    }

    // ── 3. prefer-to-board: a docked fighter boards a teammate carrier ────

    /// <summary>Item 4a — a docked bot that may serve as a gunner prefers to
    /// board a same-side, docked turret carrier with a free seat at its own
    /// station instead of launching its own ship, unless the side is down to its
    /// minimum free pilots. Falls back to launching normally on BOARD_NACK or
    /// timeout (item 2). Returns true while the bot's launch is being deferred to
    /// await/confirm a board.</summary>
    private bool TrySeekBoard(ActiveBot bot, ConnectionSession cs, double now)
    {
        var role = RoleOf(bot);
        if (!GunnerPolicy.CanServeAsGunner(role)) return false;
        if (!GunnerPolicy.ShouldSeekBoard(role, FreePilotsOnSide(bot.Side), GunnerTune)) return false;

        short myStation;
        lock (cs.Gate) myStation = cs.World.Ships.TryGetValue(bot.ShipId, out var me) ? me.StationId : (short)-1;
        if (myStation < 0) return false;

        var core = Catalog.RepresentativeCore();
        short? candidate = FindBoardCandidate(cs, core, myStation, bot.Side, bot.GunnerCarrierId);

        // A board attempt is pending against this carrier.
        if (bot.GunnerCarrierId is { } pending && candidate == pending)
        {
            // A BOARD_NACK for our attempt → fall back to launching our own ship.
            if (cs.World.BoardFailureRevision > bot.GunnerBoardRev)
            {
                bot.GunnerBoardNacks++;
                bot.GunnerCarrierId = null;
                bot.GunnerParkUntil = null;
                Console.WriteLine($"[gunner] board nack: {bot.Name} refused by carrier {pending} — falling back to launching (FedSrv.CPP:6412-6416)");
                return false;
            }
            // Give the board a bounded window to reflect (SHIP_STATUS refresh).
            if (now - bot.GunnerSeekSince < GunnerTune.BoardConfirmSeconds) return true;
            bot.GunnerCarrierId = null;   // timed out → fall back to launching normally
            Console.WriteLine($"[gunner] {bot.Name} board of {pending} not confirmed in {GunnerTune.BoardConfirmSeconds:F0}s — launching own ship");
            return false;
        }
        if (candidate is null)
        {
            bot.GunnerCarrierId = null;
            // P814b scope 3 — a turret carrier may be docked at this station and
            // GATHERING but not yet boardable this instant (its hull refresh is a
            // tick behind, or its seats just freed). Park (defer OUR launch) so the
            // carrier's gather window overlaps us instead of us insta-launching
            // straight past it. Bounded by BoardParkSeconds; then launch normally.
            if (GunnerTune.Enable && HasDockedTurretCarrierAtStation(cs, core, myStation, bot.Side))
            {
                if (bot.GunnerParkUntil is null)
                {
                    bot.GunnerParkUntil = now + GunnerTune.BoardParkSeconds;
                    Console.WriteLine($"[gunner] {bot.Name} parked at station {myStation} awaiting a docked turret carrier's gather (up to {GunnerTune.BoardParkSeconds:F0}s)");
                }
                if (now < bot.GunnerParkUntil) return true;
                bot.GunnerParkUntil = null;
            }
            return false;   // no docked carrier with a free seat → launch normally
        }

        // Initiate the board.
        bot.GunnerParkUntil = null;
        cs.SendBoardShip(candidate.Value);
        bot.GunnerCarrierId = candidate;
        bot.GunnerSeekSince = now;
        bot.GunnerBoardRev = cs.World.BoardFailureRevision;
        Console.WriteLine($"[gunner] {bot.Name} boarding teammate carrier {candidate.Value} at station {myStation} (free turret seat) instead of launching own ship");
        return true;
    }

    /// <summary>P814b scope 3 — is a same-side turret-capable carrier docked at
    /// this station right now? A gunner parks on this so it doesn't insta-launch
    /// past a carrier whose gather/refresh is a tick behind.</summary>
    private static bool HasDockedTurretCarrierAtStation(ConnectionSession cs, StaticCore core,
        short station, short side)
    {
        lock (cs.Gate)
            foreach (var s in cs.World.Ships.Values)
                if (s.SideId == side && s.StationId == station && !s.IsDead
                    && core.HullTypes.TryGetValue(s.HullId, out var hull)
                    && GunnerPolicy.TurretCapacity(hull) > 0)
                    return true;
        return false;
    }

    /// <summary>A same-side, docked turret carrier at <paramref name="station"/>
    /// with a free, equipped turret seat (native free-seat definition,
    /// teleportpane.cpp:520-531 — a gunner occupies a seat in the TURRET state;
    /// a seat is only boardable when equipped, FedSrv.CPP:2377-2392, which the
    /// scope-1 loadout guarantees). Prefers the already-chosen carrier.</summary>
    private static short? FindBoardCandidate(ConnectionSession cs, StaticCore core,
        short station, short mySide, short? prefer)
    {
        lock (cs.Gate)
        {
            // First pass: the already-chosen carrier, if still a valid candidate.
            if (prefer is { } prev && cs.World.Ships.TryGetValue(prev, out var prevShip)
                && IsBoardCandidate(cs, core, prevShip, station, mySide))
                return prev;
            foreach (var s in cs.World.Ships.Values)
                if (IsBoardCandidate(cs, core, s, station, mySide))
                    return s.ShipId;
        }
        return null;
    }

    private static bool IsBoardCandidate(ConnectionSession cs, StaticCore core,
        ShipEntity s, short station, short mySide)
    {
        // A docked ship is IsGone from the sector view (native caches departed
        // ships in the null cluster, clintlib/appmsg.cpp:475-488), so only IsDead
        // excludes a carrier here — a same-station docked turret hull is a live
        // host even though it is not in-sector-visible (P814b live finding).
        if (s.SideId != mySide || s.StationId != station || s.IsDead) return false;
        if (!core.HullTypes.TryGetValue(s.HullId, out var hull)) return false;
        int capacity = GunnerPolicy.TurretCapacity(hull);
        if (capacity <= 0) return false;
        // A free seat = fewer live TURRET-state children than turret mounts.
        int manned = 0;
        foreach (var other in cs.World.Ships.Values)
            if (other.ParentId == s.ShipId && other.State == ShipStatusMessage.StateTurret) manned++;
        return GunnerPolicy.FreeSeats(capacity, manned) > 0;
    }

    /// <summary>The side's current free pilots: participating, not-boarded bots
    /// (a proxy for "enough pilots" — never starve the economy roles, item 4).</summary>
    private int FreePilotsOnSide(short side)
    {
        int n = 0;
        lock (_gate)
            foreach (var o in _bots)
                if (IsParticipating(o) && o.Side == side && !(o.Cs?.World.Ships.TryGetValue(o.ShipId, out var me) == true && me.ParentId >= 0))
                    n++;
        return n;
    }
}
