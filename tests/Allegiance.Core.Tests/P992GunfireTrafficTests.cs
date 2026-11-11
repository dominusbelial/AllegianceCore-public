using System.Globalization;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P992 — the gunfire traffic study's reproducible half: the wire sizes of the two
/// messages, an <b>offline calculator</b> that turns a measured traffic shape into
/// the bytes each candidate option would send, the rig extrapolation arithmetic,
/// and one loopback showcase that <em>measures</em> the shape (shots, hits,
/// fan-out, per-id bytes, how many deliveries are even inside the client's draw
/// radius) so the calculator's inputs are not guesses.
/// <para>Nothing here is production code and nothing changes the wire: the
/// calculator is pure arithmetic over numbers the P983/P990 telemetry already
/// reports, and the showcase only reads it. See
/// <c>docs/reports/GUNFIRE_TRAFFIC_P992.md</c>.</para>
/// </summary>
[Collection("LiveNetwork")]   // the showcase boots a real loopback server (ephemeral port)
public sealed class P992GunfireTrafficTests
{
    // ───────────────────────── wire sizes ─────────────────────────

    /// <summary>Both messages are fixed-size and sent one message per frame, so
    /// their on-the-wire cost is exactly <c>FixedSize + CRC(4)</c>. Pinning both
    /// keeps the calculator honest if a later codec change widens them.</summary>
    [Fact]
    public void Gunfire_frames_are_fixed_size_and_framed_with_one_crc_each()
    {
        // PROJECTILE_INSTANCE(12): header 4 + timeCreate 4 + DataProjectileIGC 32
        // + position 12 + P542 launcherId 2 = 54; one CRC per packet = 58.
        Assert.Equal(54, ProjectileInstance.FixedSize);
        Assert.Equal(32, ProjectileInstance.DataProjectileSize);
        Assert.Equal(58, ProjectileInstance.FixedSize + FedPacket.CrcWidth);
        // PROJECTILE_HULL_HIT(213, this-port-only): header 4 + ids/flags/pad 8 +
        // radius 4 + position 12 + normal 12 + source 2 + pad 2 = 44; + CRC = 48.
        Assert.Equal(44, ProjectileHullHit.FixedSize);
        Assert.Equal(48, ProjectileHullHit.FixedSize + FedPacket.CrcWidth);
        Assert.True(ProjectileHullHit.Fmid > 200,
            "213 must stay above native's whole FMID range (highest native id is 200)");
    }

    /// <summary>Native's own <c>PROJECTILE_INSTANCE</c> body is the same idea minus
    /// the port's launcher id: header(4) + Time(4) + DataProjectileIGC(32) + Vector
    /// position(12) = 52 B. The port is 2 B wider (P542's <c>LauncherId</c>, needed
    /// because the port client runs no local sim to identify its own round). That
    /// is the whole difference — there is no native encumbrance to shed here.</summary>
    [Fact]
    public void Port_projectile_instance_is_native_sized_plus_the_port_launcher_id()
    {
        const int nativeBody = 4 + 4 + 32 + 12;                 // Inc/Messages.h:131-135
        Assert.Equal(nativeBody + 2, ProjectileInstance.FixedSize);
        // DataProjectileIGC = velocity(12) + forward(12) + lifespan(4) + type(2)
        // + 2 alignment padding — the layout P88 documents.
        Assert.Equal(12 + 12 + 4 + 2 + 2, ProjectileInstance.DataProjectileSize);
    }

    // ───────────────────── the offline calculator ─────────────────────

    /// <summary>A gunfire traffic shape over one window (the rig's unit: five
    /// seconds). Built from measurements, or from a hypothesis, and fed to the
    /// option calculators below.</summary>
    /// <param name="Shots">Real rounds fired in the window.</param>
    /// <param name="Hits">Rounds that raised a hull-hit event.</param>
    /// <param name="Recipients">Peers in the shooter's sector that receive one
    /// shot's frame (measured by the showcase's fan-out identity).</param>
    /// <param name="Ticks">Broadcast ticks in the window (20 Hz × 5 s = 100).</param>
    /// <param name="Sectors">Sectors the shooters are spread over (3 on the rig).</param>
    /// <param name="NearFraction">Fraction of (shot, recipient) deliveries whose
    /// round spawns inside the client's own projectile draw radius — the
    /// renderer's own cull, <c>ProjectileVisibility.VisibilityRadius</c> = 5900 u.</param>
    /// <param name="DependencyBytes">The measured pose/export/status bytes the
    /// <em>fire relay itself</em> adds over the whole window: the
    /// SINGLE_SHIP_UPDATE(78) refresh (2 959 200 B) plus the first-sight
    /// EXPORT(26) (335 376 B) and SHIP_STATUS(90) (345 744 B) the emission path
    /// orders ahead of the round — see the showcase's stock/delta reconciliation.</param>
    internal readonly record struct TrafficShape(
        long Shots, long Hits, int Recipients, int Ticks, int Sectors,
        double NearFraction, long DependencyBytes = 3_640_320)
    {
        internal double Deliveries => Shots * (double)Recipients;
        internal double HitDeliveries => Hits * (double)Recipients;
        internal double ShotDeliveriesPerRecipientTick => Deliveries / (Math.Max(1, Sectors * Ticks) * (double)Recipients);
        internal double HitDeliveriesPerRecipientTick => HitDeliveries / (Math.Max(1, Sectors * Ticks) * (double)Recipients);
        /// <summary>One batched frame per recipient per tick: the number of
        /// frames a native-style var-item array would need.</summary>
        internal double BatchedFrames => Deliveries / Math.Max(1e-9, ShotDeliveriesPerRecipientTick);
    }

    /// <summary>Frame sizes the calculator charges.</summary>
    internal static class Frames
    {
        internal const int Projectile = ProjectileInstance.FixedSize + FedPacket.CrcWidth;   // 58
        internal const int HullHit = ProjectileHullHit.FixedSize + FedPacket.CrcWidth;       // 48
        internal const int Crc = FedPacket.CrcWidth;                                        // 4
        internal const int Header = 4;                                                      // cbmsg + fmid
        /// <summary>Compact PROJECTILE_INSTANCE payload: type 2 + quantised
        /// forward 4 + speed 2 + quantised sector-local position 6 + launcher 2 +
        /// lifespan-override flag 1 + pad 3 = 20 (a 4-byte-aligned record). The
        /// 12-byte vectors and the 4-byte float lifespan are what it replaces.</summary>
        internal const int CompactProjectilePayload = 20;
        /// <summary>Compact PROJECTILE_HULL_HIT payload: target 2 + cluster 2 +
        /// flags 1 + pad 1 + quantised radius 2 + quantised position 6 + two-angle
        /// normal 4 + source 2 + pad 2 = 22.</summary>
        internal const int CompactHullHitPayload = 22;
    }

    /// <summary>Today: one 58-byte frame per (shot, recipient) and one 48-byte
    /// frame per (hit, recipient).</summary>
    internal static (double Projectiles, double Hits) Baseline(in TrafficShape s) =>
        (s.Deliveries * Frames.Projectile, s.HitDeliveries * Frames.HullHit);

    /// <summary>Option (b) — batch: native-style var-item arrays, one frame per
    /// (recipient, tick) carrying every round and every hit of that tick, so the
    /// per-message <c>cbmsg+fmid</c> headers disappear and the CRC is paid once.</summary>
    internal static (double Projectiles, double Hits) Batched(in TrafficShape s)
    {
        double frames = s.BatchedFrames;
        const int projectilePayload = Frames.Projectile - Frames.Header - Frames.Crc;   // 50
        const int hullHitPayload = Frames.HullHit - Frames.Header - Frames.Crc;         // 40
        return (frames * (Frames.Header + Frames.Crc) + s.Deliveries * projectilePayload,
                frames * (Frames.Header + Frames.Crc) + s.HitDeliveries * hullHitPayload);
    }

    /// <summary>Option (c) — compact records, still one message per frame.</summary>
    internal static (double Projectiles, double Hits) Compact(in TrafficShape s) =>
        (s.Deliveries * (Frames.Header + Frames.CompactProjectilePayload + Frames.Crc),
         s.HitDeliveries * (Frames.Header + Frames.CompactHullHitPayload + Frames.Crc));

    /// <summary>Options (b)+(c): compact records in one per-tick batch.</summary>
    internal static (double Projectiles, double Hits) CompactBatched(in TrafficShape s)
    {
        double frames = s.BatchedFrames;
        return (frames * (Frames.Header + Frames.Crc) + s.Deliveries * Frames.CompactProjectilePayload,
                frames * (Frames.Header + Frames.Crc) + s.HitDeliveries * Frames.CompactHullHitPayload);
    }

    /// <summary>Option (d) — interest culling: a round goes only to recipients whose
    /// own ship is inside the client's own projectile draw radius of its spawn
    /// point (the renderer discards everything further away:
    /// <c>ProjectileVisibility</c>), and a hull hit only to those plus the shooter
    /// (see the report's bot-consumer note).</summary>
    internal static (double Projectiles, double Hits) Culled(in TrafficShape s) =>
        (Baseline(s).Projectiles * s.NearFraction, Baseline(s).Hits * s.NearFraction);

    /// <summary>Stock: both lanes plus the pose/export/status dependency the fire
    /// relay adds.</summary>
    internal static double StockTotal(in TrafficShape s)
    {
        var (pj, hit) = Baseline(s);
        return pj + hit + s.DependencyBytes;
    }

    /// <summary>THE RIG-SHAPED FIXTURE (measured by the showcase below: 411 peers,
    /// 3 sectors, 36 shooters, 100 ticks = 5 s): 1 764 shots → 1 440 hits (81.6 %),
    /// 137 recipients per shot, 5.88 rounds per recipient per tick, and 62.0 % of
    /// deliveries inside the 5 900 u draw radius.</summary>
    internal static TrafficShape RigShaped() => new(
        Shots: 1764, Hits: 1440, Recipients: 137, Ticks: 100, Sectors: 3,
        NearFraction: 149940.0 / 241668.0);

    // ───────────────────── calculator pins ─────────────────────

    /// <summary>The stock arm must reproduce the bytes the showcase actually
    /// measured (14 016 744 B for 12, 9 469 440 B for 213), or the option savings
    /// below are fiction.</summary>
    [Fact]
    public void Offline_calculator_reproduces_the_measured_fixture_bytes()
    {
        var s = RigShaped();
        var (pj, hits) = Baseline(s);
        Assert.Equal(14_016_744, (long)pj);      // 241 668 frames × 58 B
        Assert.Equal(9_469_440, (long)hits);     // 197 280 frames × 48 B
        Assert.Equal(241_668, (long)s.Deliveries);
        Assert.Equal(197_280, (long)s.HitDeliveries);
        Assert.Equal(5.88, s.ShotDeliveriesPerRecipientTick, 2);
        Assert.Equal(4.80, s.HitDeliveriesPerRecipientTick, 2);
        // Measured stock: 12 + 213 + the fire relay's pose/export/status bytes.
        Assert.Equal(27_126_504, (long)StockTotal(s));
    }

    /// <summary>The option table the report quotes, in bytes over the rig-shaped
    /// five-second window and as a saving on the stock gunfire cost. Pinned so the
    /// report's numbers cannot drift away from the code that computes them.</summary>
    [Fact]
    public void Option_table_matches_the_report()
    {
        var s = RigShaped();
        double stock = StockTotal(s);
        var (b12, b213) = Baseline(s);
        var (batch12, batch213) = Batched(s);
        var (compact12, compact213) = Compact(s);
        var (cb12, cb213) = CompactBatched(s);
        var (cull12, cull213) = Culled(s);

        // (b) batching: −11.4 % of 12, −13.2 % of 213 (header + once-per-frame CRC).
        Assert.Equal(12_412_200, (long)batch12);
        Assert.Equal(8_220_000, (long)batch213);
        // (c) compact: −51.7 % of 12, −37.5 % of 213.
        Assert.Equal(6_766_704, (long)compact12);
        Assert.Equal(5_918_400, (long)compact213);
        // (b)+(c): −63.2 % of 12, −50.7 % of 213.
        Assert.Equal(5_162_160, (long)cb12);
        Assert.Equal(4_668_960, (long)cb213);
        // (d) the renderer-radius cull keeps 62.0 % of both lanes.
        Assert.Equal(8_696_520, (long)cull12);
        Assert.Equal(5_875_200, (long)cull213);

        double lanes = b12 + b213;
        double deps = s.DependencyBytes;
        double OfStock(double then) => 100 * (1 - (then + deps) / stock);
        // Savings on the two lanes ... and on the whole stock gunfire cost (which
        // includes the fire relay's 3.64 MB of pose/export/status dependency).
        Assert.Equal(12.2, 100 * (1 - (batch12 + batch213) / lanes), 1);
        Assert.Equal(46.0, 100 * (1 - (compact12 + compact213) / lanes), 1);
        Assert.Equal(58.1, 100 * (1 - (cb12 + cb213) / lanes), 1);
        Assert.Equal(38.0, 100 * (1 - (cull12 + cull213) / lanes), 1);
        Assert.Equal(10.5, OfStock(batch12 + batch213), 1);
        Assert.Equal(39.8, OfStock(compact12 + compact213), 1);
        Assert.Equal(50.3, OfStock(cb12 + cb213), 1);
        Assert.Equal(32.9, OfStock(cull12 + cull213), 1);
        Assert.Equal(100.0, 100 * (stock - 0) / stock, 1);   // (a) native model removes the stock entirely

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[p992-options] stock[12={b12 / 1e6:0.000}MB 213={b213 / 1e6:0.000}MB " +
            $"deps={s.DependencyBytes / 1e6:0.000}MB total={stock / 1e6:0.000}MB] " +
            $"batch[12={100 * (1 - batch12 / b12):0.0}% 213={100 * (1 - batch213 / b213):0.0}%] " +
            $"compact[12={100 * (1 - compact12 / b12):0.0}% 213={100 * (1 - compact213 / b213):0.0}%] " +
            $"compactBatch[12={100 * (1 - cb12 / b12):0.0}% 213={100 * (1 - cb213 / b213):0.0}%] " +
            $"cull[12={100 * (1 - cull12 / b12):0.0}% 213={100 * (1 - cull213 / b213):0.0}%] " +
            $"ofLanes[batch={100 * (1 - (batch12 + batch213) / lanes):0.0}% " +
            $"compact={100 * (1 - (compact12 + compact213) / lanes):0.0}% " +
            $"compactBatch={100 * (1 - (cb12 + cb213) / lanes):0.0}% " +
            $"cull={100 * (1 - (cull12 + cull213) / lanes):0.0}%] " +
            $"ofStock[batch={OfStock(batch12 + batch213):0.0}% " +
            $"compact={OfStock(compact12 + compact213):0.0}% " +
            $"compactBatch={OfStock(cb12 + cb213):0.0}% " +
            $"cull={OfStock(cull12 + cull213):0.0}%]"));
    }

    /// <summary>How the rig's quoted window is explained: one 58-byte frame per
    /// (shot, recipient), so the quoted 574 000 messages in 5 s over ~136 visible
    /// recipients is ~844 shots/s — ~2.1 shots/s per bot at 400 bots, i.e. ~21 %
    /// of the bots firing at a 10/s gun cadence, or every bot firing ~21 % of the
    /// time. The window's own byte figure follows from the same product.</summary>
    [Fact]
    public void Rig_window_implies_the_shot_rate_and_the_frame_bytes()
    {
        const long rigMessages = 574_000;      // Claude's b105 window, PROJECTILE_INSTANCE(12)
        const double windowSecs = 5;
        const int recipients = 136;            // P990's measured visible ships per peer
        const int bots = 400;
        double shotsPerSec = rigMessages / windowSecs / recipients;
        Assert.Equal(844, shotsPerSec, 0);
        Assert.Equal(2.11, shotsPerSec / bots, 1);
        Assert.Equal(33_292_000, rigMessages * Frames.Projectile);   // the 12 lane's bytes
        // 574 000 × 58 B = 58.8 % of the window's ordered lane → a clash-peak window.
        double orderedLane = rigMessages * Frames.Projectile / 0.588;
        Assert.InRange(orderedLane, 56e6, 57e6);
        // The same window at the run-average 47.7 % ordered-lane share.
        double egress = orderedLane / 0.477;
        Assert.InRange(egress, 118e6, 119e6);
    }

    /// <summary>Option savings expressed as a share of total server egress, using
    /// the rig's own quoted shares (12 = 58.8 % of the ordered lane, ordered =
    /// 47.7 % of egress, 213 = 20.1 % of the ordered lane) rather than the
    /// fixture's, whose EXPORT baseline is much larger than the rig's.</summary>
    [Fact]
    public void Option_savings_as_a_share_of_rig_egress()
    {
        const double share12 = 0.588 * 0.477;   // 28.0 % of egress
        const double share213 = 0.201 * 0.477;  // 9.6 % of egress
        Assert.Equal(0.280, share12, 3);
        Assert.Equal(0.096, share213, 3);

        var s = RigShaped();
        double b12 = Baseline(s).Projectiles, b213 = Baseline(s).Hits;
        double Save(double now, double then) => 1 - then / now;
        double Rig(double s12, double s213) => (share12 * s12 + share213 * s213) * 100;

        // (b): 4.5 % of egress; (c): 18.1 %; (b)+(c): 22.6 %; (d): 14.3 %;
        // (d) on 12 only plus (b)+(c) on 213: 15.5 %; (a): 37.6 %.
        Assert.Equal(4.5, Rig(0.114, 0.132), 1);
        Assert.Equal(18.1, Rig(0.517, 0.375), 1);
        Assert.Equal(22.6, Rig(0.632, 0.507), 1);
        Assert.Equal(14.3, (share12 + share213) * 0.380 * 100, 1);
        Assert.Equal(15.5, (share12 * 0.380 + share213 * 0.507) * 100, 1);
        Assert.Equal(37.6, (share12 + share213) * 100, 1);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[p992-rig-egress] share12={share12 * 100:0.0}% share213={share213 * 100:0.0}% " +
            $"batch={Rig(Save(b12, Batched(s).Projectiles), Save(b213, Batched(s).Hits)):0.0}% " +
            $"compact={Rig(Save(b12, Compact(s).Projectiles), Save(b213, Compact(s).Hits)):0.0}% " +
            $"compactBatch={Rig(Save(b12, CompactBatched(s).Projectiles), Save(b213, CompactBatched(s).Hits)):0.0}% " +
            $"cull={Rig(Save(b12, Culled(s).Projectiles), Save(b213, Culled(s).Hits)):0.0}% " +
            $"native={(share12 + share213) * 100:0.0}%"));
    }

    // ───────────────────── the measured showcase ─────────────────────

    private static ServerConfig Config() => new()
    {
        ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
        Profiler = new() { Enabled = false, BroadcastBudget = true },
        Broadcast = new() { MinUpdateHz = 2, ShipUpdateModel = "native", ParallelBuild = true,
            // P992 measured the pre-gate wire (SHIP_STATUS refreshes on); the 13.4 % dependency
            // share below is that report's number, so pin the gate off (default ON since 74d07f5a).
            ShipStatusChangeGate = false },
        Games = [new() { Name = "P992", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    internal sealed record Traffic(
        int Peers, int FiringShips, int Ticks, long Shots, long Hits,
        long ShotPairs, long ShotPairsNear5900, long HitPairs, long HitPairsNear5900,
        long Msgs12, long Bytes12, long Msgs213, long Bytes213, long Msgs78, long Bytes78,
        long Msgs26, long Bytes26, long Msgs90, long Bytes90,
        long TotalBytes, long HeavyBytes, long KeyedBytes, long OtherBytes, long OtherMsgs,
        double WallSeconds, double SimSeconds);

    internal static Traffic Run(int peers, int firingShips, int ticks, bool aligned = true, int sectors = 1, float spacing = 150f, Action<string>? log = null)
    {
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config());
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;
        var core = mission.Core;
        // A fighter hull with real hardpoints, armed from the hull's own preferred
        // weapon list (the native drone loadout, Ship.MountPreferredParts) — one
        // weapon per ship so the shot rate is the weapon's own dtimeBurst.
        var hull = core.HullTypes[55];      // "Fighter" (PCore001), 3 fixed mounts
        DataPartTypeIGC weaponPart = default;
        foreach (short id in hull.PreferredPartsTypes)
        {
            if (id == -1) break;
            if (core.PartTypes.TryGetValue(id, out var candidate) && candidate.EquipmentType == EquipmentType.Weapon)
            {
                weaponPart = candidate;
                break;
            }
        }
        var weapon = (DataPartTypeIGC.Weapon)weaponPart.Type;

        var ships = new List<Ship>(peers);
        var peerList = new List<(ConnectionId, Ship, Sector)>(peers);
        for (int i = 0; i < peers; i++)
        {
            short side = (short)((i / sectors) % 2);
            int sec = i % sectors;
            int rank = i / (2 * sectors);
            // Far from every map asteroid so the mining pass never triggers on a
            // parked combat fixture (IsReadyToMine is a proximity test).
            float x = 50000f + sec * 8000f + rank * spacing;
            float z = side == 0 ? 300f : -300f;
            var orientation = Orientation.Identity;
            if (side == 1) orientation.Yaw(MathF.PI);        // face the other line
            if (!aligned) orientation.Yaw(side == 1 ? MathF.PI * 0.5f : -MathF.PI * 0.5f);  // fire past each other
            var ship = new Ship((short)(10000 + i), hull, new Vector3(x, 0, z), Vector3.Zero, orientation)
            {
                SideId = side,
            };
            var shipSector = server.TestSectors[sec];
            mission.PlaceShip(ship, shipSector);
            ship.MountWeapon(weapon, 0);
            ships.Add(ship);
            peerList.Add((new ConnectionId((uint)(700 + i)), ship, shipSector));
        }
        server.TestSeedPeers(peerList);

        // Option (d) measurement: how many (shot, recipient) deliveries are even
        // within the client's own draw radius (ProjectileVisibility.VisibilityRadius,
        // 5900 u) of the round's spawn point. Today every peer in the sector gets
        // the frame regardless.
        long shotPairs = 0, shotPairsNear = 0, hitPairs = 0, hitPairsNear = 0;
        float nearSq = Allegiance.ClientCore.State.ProjectileVisibility.VisibilityRadiusSqr;
        if (mission.Site is ServerHost host)
        {
            host.ProjectileCreated += p =>
            {
                foreach (var (_, viewer, _) in peerList)
                {
                    if (!ReferenceEquals(viewer.Sector, p.OwnerSector)) continue;
                    shotPairs++;
                    if ((viewer.Position - p.Position).LengthSquared() <= nearSq) shotPairsNear++;
                }
            };
            host.ProjectileHit += (p, impact, _, _, _, _, _) =>
            {
                foreach (var (_, viewer, _) in peerList)
                {
                    if (!ReferenceEquals(viewer.Sector, p.OwnerSector)) continue;
                    hitPairs++;
                    if ((viewer.Position - impact).LengthSquared() <= nearSq) hitPairsNear++;
                }
            };
        }

        foreach (var s in ships) s.SetFraction(1f);
        for (int i = 0; i < firingShips && i < ships.Count; i++) ships[i].StateM = ShipControlState.AllWeapons;

        var telemetry = server.BroadcastBudgetTelemetry!;
        telemetry.SetSecond(1000);                         // pin: one bucket, exact totals
        const double dt = 1.0 / 20.0;
        double startWall = telemetry.Clock();
        double now = mission.LastUpdate;
        server.TestUpdateCadenceClock = () => now;         // deterministic cadence clock (sim time)
        for (uint tick = 0; tick < ticks; tick++)
        {
            now += dt;
            // Sustained-fire fixture: keep the shooters alive and loaded so the
            // shot rate is the weapon cadence for the whole window.
            foreach (var s in ships)
            {
                s.SetFraction(1f);
                s.Ammo = hull.MaxAmmo;
                s.Energy = s.EffectiveMaxEnergy();
            }
            mission.Update(now);
            telemetry.SimNowMs = now * 1000.0;
            server.TestProjectRecordedTick(tick, snapshot: true);
            server.FlushBroadcastSnapshots();
        }
        double wall = telemetry.Clock() - startWall;
        long shots = mission.Sides.Sum(s => mission.Skills!.Count(s.Id, ShowcaseSkill.Guns));
        string report = server.TakeBroadcastBudgetReport(1000, Math.Max(0.001, wall));
        log?.Invoke(report);
        long Msgs(int fmid) => telemetry.OrderedLaneWindowMessages(fmid, 1000);
        long Bytes(int fmid) => telemetry.OrderedLaneWindowBytes(fmid, 1000);
        // P364 hit events are raised once per landed round; each one is fanned out
        // to every peer in the sector, so hits = 213 messages / peers.
        long fanout = Math.Max(1, peers / Math.Max(1, sectors));
        long hits = Msgs(ProjectileHullHit.Fmid) / fanout;
        return new Traffic(peers, firingShips, ticks, shots, hits, shotPairs, shotPairsNear, hitPairs, hitPairsNear,
            Msgs(ProjectileInstance.Fmid), Bytes(ProjectileInstance.Fmid),
            Msgs(ProjectileHullHit.Fmid), Bytes(ProjectileHullHit.Fmid),
            Msgs(SingleShipUpdate.Fmid), Bytes(SingleShipUpdate.Fmid),
            Msgs(ExportShip.Fmid), Bytes(ExportShip.Fmid),
            Msgs(ShipStatusMessage.Fmid), Bytes(ShipStatusMessage.Fmid),
            telemetry.WindowTotal(1000),
            telemetry.WindowBytes(BroadcastCategory.HeavyShipUpdates, 1000),
            telemetry.WindowBytes(BroadcastCategory.ReliableKeyed, 1000),
            telemetry.WindowBytes(BroadcastCategory.Other, 1000),
            telemetry.WindowMessages(BroadcastCategory.Other, 1000),
            wall, now - mission.LastUpdate + ticks * dt);
    }

    internal static void Print(Traffic r)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[p992] peers={r.Peers} firing={r.FiringShips} ticks={r.Ticks} shots={r.Shots} hits={r.Hits} " +
            $"shotPairs={r.ShotPairs} near5900={r.ShotPairsNear5900} hitPairs={r.HitPairs} hitNear5900={r.HitPairsNear5900} " +
            $"m12={r.Msgs12} b12={r.Bytes12} m213={r.Msgs213} b213={r.Bytes213} m78={r.Msgs78} b78={r.Bytes78} " +
            $"m26={r.Msgs26} b26={r.Bytes26} m90={r.Msgs90} b90={r.Bytes90} " +
            $"other={r.OtherBytes} omsgs={r.OtherMsgs} heavy={r.HeavyBytes} keyed={r.KeyedBytes} total={r.TotalBytes} wallS={r.WallSeconds:0.00}"));
    }

    /// <summary>The study's fixture (rig shape): 411 peers over 3 sectors, two
    /// opposed lines of armed fighters with 36 of them firing, 100 recorded ticks
    /// (5 s at 20 Hz) of the real sim + the real broadcast suffix, P990 telemetry
    /// ON. It is <b>measurement only</b>: the same server, the same wire.
    /// <para>The identities this test pins are the calculator's inputs: every shot
    /// is one PROJECTILE_INSTANCE frame per peer in the shooter's sector (58 B
    /// framed), every landed round is one PROJECTILE_HULL_HIT frame per peer
    /// (48 B framed), and the fire relay's own pose/export/status frames are the
    /// difference between a firing and a non-firing fixture of the same shape.</para></summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Loopback_showcase_measures_gunfire_traffic_and_the_audience_radius()
    {
        Require.Artwork();
        const int peers = 411, sectors = 3, firing = 36, ticks = 100;
        var firingRun = Run(peers, firing, ticks, aligned: true, sectors: sectors);
        Print(firingRun);
        var quietRun = Run(peers, 0, ticks, aligned: true, sectors: sectors);
        Print(quietRun);

        int fanout = peers / sectors;                       // every peer in the shooter's sector
        long shotDeliveries = firingRun.Shots * fanout;
        // One frame per shot per recipient, exactly.
        Assert.Equal(shotDeliveries, firingRun.Msgs12);
        Assert.Equal(shotDeliveries * (ProjectileInstance.FixedSize + FedPacket.CrcWidth), firingRun.Bytes12);
        // One frame per landed round per recipient; the events themselves come from
        // the sim's own hit seam, so this also cross-checks the hit count.
        Assert.True(firingRun.Hits > 0, "the showcase landed no hits");
        Assert.Equal(firingRun.Hits * fanout, firingRun.Msgs213);
        Assert.Equal(firingRun.Hits * fanout * (ProjectileHullHit.FixedSize + FedPacket.CrcWidth), firingRun.Bytes213);
        // The quiet fixture of the same shape sends none of either.
        Assert.Equal(0, quietRun.Msgs12);
        Assert.Equal(0, quietRun.Msgs213);

        // The measured shape is the calculator's input (2 % tolerance).
        Assert.InRange(firingRun.Shots, 1764 * 0.98, 1764 * 1.02);
        Assert.InRange(firingRun.Hits, 1440 * 0.98, 1440 * 1.02);
        Assert.InRange((double)firingRun.ShotPairsNear5900 / firingRun.ShotPairs, 0.60, 0.64);

        // The fire relay's pose/export/status dependency is the whole stock delta:
        // 78 refresh + the first-sight EXPORT/STATUS the emission path orders ahead
        // of a round. It is the measure of what a "no per-shot message" model would
        // also remove.
        long dependency = firingRun.Bytes78 - quietRun.Bytes78
                        + (firingRun.Bytes26 - quietRun.Bytes26)
                        + (firingRun.Bytes90 - quietRun.Bytes90);
        long lanes = firingRun.Bytes12 + firingRun.Bytes213;
        long delta = firingRun.TotalBytes - quietRun.TotalBytes;
        Assert.Equal(13.4, 100.0 * dependency / (lanes + dependency), 1);  // 13.4 % of the stock gunfire cost
        Assert.True(Math.Abs(delta - (lanes + dependency)) <= delta / 100,
            $"the firing delta {delta} is not the two lanes {lanes} plus the dependency {dependency}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[p992-stock] shots={firingRun.Shots} hits={firingRun.Hits} fanout={fanout} " +
            $"b12={firingRun.Bytes12} b213={firingRun.Bytes213} dep78={firingRun.Bytes78 - quietRun.Bytes78} " +
            $"dep26={firingRun.Bytes26 - quietRun.Bytes26} dep90={firingRun.Bytes90 - quietRun.Bytes90} " +
            $"stock={lanes + dependency} delta={delta} quietTotal={quietRun.TotalBytes} fireTotal={firingRun.TotalBytes} " +
            $"near5900={firingRun.ShotPairsNear5900}/{firingRun.ShotPairs}"));
    }

    /// <summary>Re-measure any other fixture shape without editing the test
    /// (<c>P992_PEERS</c>, <c>P992_FIRING</c>, <c>P992_TICKS</c>,
    /// <c>P992_SECTORS</c>, <c>P992_SPACING</c>, <c>P992_ALIGNED=0</c>) — how the
    /// report's scaling and intensity rows were taken.</summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Gunfire_fixture_measurement_from_environment()
    {
        Require.Artwork();
        static int Env(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : fallback;
        int peers = Env("P992_PEERS", 144);
        int firing = Env("P992_FIRING", 16);
        int ticks = Env("P992_TICKS", 200);
        int sectors = Env("P992_SECTORS", 1);
        float spacing = Env("P992_SPACING", 150);
        bool aligned = Environment.GetEnvironmentVariable("P992_ALIGNED") != "0";
        var r = Run(peers, firing, ticks, aligned, sectors, spacing);
        Print(r);
        Assert.Equal(firing > 0, r.Msgs12 > 0);
    }
}
