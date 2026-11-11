using System.Globalization;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P993 — <c>broadcast.fireAudienceCull</c>: the <c>PROJECTILE_INSTANCE(12)</c>
/// relay (ship and probe rounds) is sent only to viewers that can actually DRAW
/// the round — the client's own <see cref="ProjectileDrawRadius.WorldUnits"/>
/// (5 900 u) reach plus the round's and the viewer's travel during the round's
/// lifespan — while the shooter's own peer, admin free-camera views and the whole
/// <c>PROJECTILE_HULL_HIT(213)</c> audience are untouched.
/// <para>The tests come in three layers: <b>pure</b> (the shared radius, the flag
/// grammar, the margin rule and a brute-force proof that the rule never removes a
/// round the renderer could draw), <b>live loopback</b> (a real server, one real
/// shot, per-recipient frames decoded from the wire: who gets the round and the
/// <c>EnsureEmissionPose</c> dependency), and the <b>acceptance</b> fixture (every
/// round the client's own <see cref="ProjectileVisibility"/> would draw with the
/// flag off is still delivered with it on). See
/// <c>docs/reports/FIRE_AUDIENCE_CULL_P993.md</c>.</para>
/// </summary>
[Collection("LiveNetwork")]   // boots real in-process servers; keep serialized with other live suites
public sealed class P993FireAudienceCullTests
{
    // ───────────────────────── the shared radius ─────────────────────────

    /// <summary>The relay and the renderer must be ONE number: the server links
    /// Core (never the client), so the value lives in Core and
    /// <c>ProjectileVisibility</c> — the renderer's own cull — references it.</summary>
    [Fact]
    public void The_relay_and_the_renderer_share_one_draw_radius()
    {
        Assert.Equal(5900f, ProjectileDrawRadius.WorldUnits);
        Assert.Equal(ProjectileDrawRadius.WorldUnits, ProjectileVisibility.VisibilityRadius);
        Assert.Equal(ProjectileDrawRadius.WorldUnitsSquared, ProjectileVisibility.VisibilityRadiusSqr);
        Assert.Equal(ProjectileVisibility.VisibilityRadius * ProjectileVisibility.VisibilityRadius,
            ProjectileVisibility.VisibilityRadiusSqr);
    }

    // ───────────────────────── the flag grammar ─────────────────────────

    [Theory]
    [InlineData(false, null, false)]      // absent env + default config = OFF
    [InlineData(true, null, true)]        // config wins when the env is absent
    [InlineData(false, "1", true)]        // env wins over config
    [InlineData(true, "0", false)]
    [InlineData(false, " true ", true)]   // trimmed, case-insensitive
    [InlineData(true, "FALSE", false)]
    [InlineData(true, "", true)]          // empty env preserves the config
    public void Flag_resolves_config_then_environment(bool configured, string? environment, bool expected)
        => Assert.Equal(expected, Srv.ResolveFireAudienceCullFlag(configured, environment));

    [Fact]
    public void Flag_grammar_rejects_anything_but_0_1_false_true()
        => Assert.Throws<InvalidDataException>(() => Srv.ResolveFireAudienceCullFlag(false, "yes"));

    [Fact]
    public void Flag_defaults_to_off()
        => Assert.False(new BroadcastConfig().FireAudienceCull,
            "broadcast.fireAudienceCull must be default OFF (the rig A/B decides)");

    // ───────────────────────── the margin rule ─────────────────────────

    /// <summary>A Gatling-like round: 1 000 u/s, 1.5 s life ⇒ at most 1 500 u of
    /// travel. The margin therefore reaches 1 500 u past the bare 5 900 u
    /// radius — exactly the brief's "spawned just outside 5 900 flying at the
    /// viewer" case, which must be KEPT (the client draws it once it is inside).</summary>
    [Fact]
    public void The_margin_keeps_a_round_that_would_enter_the_draw_radius()
    {
        var round = new Srv.EmissionCullOrigin(new Vector3(0, 0, 0), Speed: 1000f, Lifespan: 1.5f);

        // A parked viewer 7 000 u down-range: outside the radius, inside R + 1 500.
        Assert.True(Srv.ProjectileDrawRangeKeeps(new Vector3(0, 0, -7000), 0f, round));
        // 7 500 u: 1 600 > the round's own 1 500 u of travel — but the viewer is
        // allowed to close the remaining 100 u itself (200 u/s × 1.5 s = 300 u).
        Assert.True(Srv.ProjectileDrawRangeKeeps(new Vector3(0, 0, -7500), 200f, round));
        // Beyond both travel budgets: no pixel can ever be produced.
        Assert.False(Srv.ProjectileDrawRangeKeeps(new Vector3(0, 0, -7500), 0f, round));
        Assert.False(Srv.ProjectileDrawRangeKeeps(new Vector3(0, 0, -40000), 200f, round));
        // Inside the bare radius is always kept.
        Assert.True(Srv.ProjectileDrawRangeKeeps(new Vector3(0, 0, -5000), 0f, round));
        Assert.True(Srv.ProjectileDrawRangeKeeps(new Vector3(0, 0, 0), 0f, round));
        // The lifetime bound is the type's lifespan, so a longer-lived round has a
        // longer reach (the same speed, twice the life).
        var longLived = round with { Lifespan = 3f };
        Assert.True(Srv.ProjectileDrawRangeKeeps(new Vector3(0, 0, -8800), 0f, longLived));
        Assert.False(Srv.ProjectileDrawRangeKeeps(new Vector3(0, 0, -8800), 0f, round));
    }

    /// <summary>The per-recipient decision, including the two never-cull kinds:
    /// the shooter's own peer (own-echo de-dup + the bot own-round counter) and an
    /// admin free-camera view.</summary>
    [Fact]
    public void The_shooter_and_admin_views_are_never_culled_and_the_flag_off_path_always_keeps()
    {
        var far = new Vector3(0, 0, -40000);
        var round = new Srv.EmissionCullOrigin(Vector3.Zero, Speed: 1000f, Lifespan: 1.5f);
        // Flag off: every recipient is kept, whatever the distance (byte-identical wire).
        Assert.True(Srv.FireAudienceCullKeeps(cull: false, shooterOwnPeer: false, adminView: false, far, 0f, round));
        // Flag on, an ordinary viewer beyond the reach: culled.
        Assert.False(Srv.FireAudienceCullKeeps(cull: true, shooterOwnPeer: false, adminView: false, far, 0f, round));
        // Flag on, the shooter's own peer: kept (its own copy drives own-echo de-dup).
        Assert.True(Srv.FireAudienceCullKeeps(cull: true, shooterOwnPeer: true, adminView: false, far, 0f, round));
        // Flag on, an admin free-camera view: kept (P612's unfiltered debug view).
        Assert.True(Srv.FireAudienceCullKeeps(cull: true, shooterOwnPeer: false, adminView: true, far, 0f, round));
    }

    /// <summary>Brute-force proof of the rule's safety. For 20 000 random
    /// (spawn, viewer, velocity, lifespan, viewer-speed-limit) combinations: if
    /// ANY point on the round's path at ANY time within its lifespan comes within
    /// the renderer's radius of the viewer — with the viewer moving towards the
    /// round as fast as its speed limit allows, the most favourable motion there
    /// is — then the rule must keep the delivery. A failure means the cull could
    /// remove a round the client would have drawn.</summary>
    [Fact]
    public void The_cull_never_removes_a_round_the_client_could_draw()
    {
        float rSq = ProjectileDrawRadius.WorldUnitsSquared;
        var rng = new Random(993);
        float Coord() => (float)((rng.NextDouble() - 0.5) * 40000.0);
        for (int i = 0; i < 20_000; i++)
        {
            var spawn = new Vector3(Coord(), Coord(), Coord());
            var viewer = new Vector3(Coord(), Coord(), Coord());
            float speed = (float)(rng.NextDouble() * 4000.0);
            float life = (float)(0.1 + rng.NextDouble() * 4.0);
            float viewerLimit = (float)(rng.NextDouble() * 500.0);
            var heading = new Vector3(Coord(), Coord(), Coord()).Normalize();
            if (heading.LengthSquared() == 0f) heading = new Vector3(0, 0, 1);
            var origin = new Srv.EmissionCullOrigin(spawn, speed, life);
            bool kept = Srv.ProjectileDrawRangeKeeps(viewer, viewerLimit, origin);

            bool drawable = false;
            for (int step = 0; step <= 48 && !drawable; step++)
            {
                float t = life * step / 48f;
                var roundPos = spawn + heading * (speed * t);
                // Adversarial viewer: move as far as allowed, straight at the round.
                var toRound = roundPos - viewer;
                float reach = MathF.Min(viewerLimit * t, toRound.Length());
                var viewerPos = toRound.LengthSquared() > 0f ? viewer + toRound.Normalize() * reach : viewer;
                if ((roundPos - viewerPos).LengthSquared() <= rSq) drawable = true;
            }
            if (drawable)
                Assert.True(kept, $"culled a drawable round: spawn={spawn} viewer={viewer} v={speed} life={life} viewerLimit={viewerLimit}");
        }
    }

    // ───────────────────────── live: one real shot ─────────────────────────

    private sealed record Frame(uint Tick, ConnectionId Peer, int Key, bool Guaranteed, byte[] Bytes);

    private static ushort FmidOf(byte[] message) => (ushort)(message[2] | message[3] << 8);

    private static IEnumerable<byte[]> Messages(Frame frame) => FedPacket.Unpack(frame.Bytes);

    private static bool Carries(Frame frame, ushort fmid) => Messages(frame).Any(m => FmidOf(m) == fmid);

    /// <summary>A live loopback range: one started mission, real ships, real
    /// weapons, the real broadcast pipeline, and a per-peer capture of every
    /// published frame. Ships are added before <see cref="Start"/>; shooters fire
    /// with a one-round ammo cap so a test sees exactly one shot.</summary>
    private sealed class Range : IDisposable
    {
        internal readonly Srv Server;
        internal readonly Mission Mission;
        internal readonly Sector Sector;
        internal readonly DataHullTypeIGC Hull;
        internal readonly DataPartTypeIGC Weapon;
        internal readonly List<(ConnectionId Conn, Ship Ship)> Peers = new();
        internal readonly List<Frame> Frames = new();
        internal readonly List<Ship> Ships = new();
        internal readonly List<(uint Tick, Ship Ship)> Positions = new();
        private readonly List<(ConnectionId, Ship, Sector)> _pending = new();
        private readonly bool _profiling;
        private readonly bool _capture;
        private readonly bool _recordPositions;
        internal readonly List<double> TickWallMs = new();
        internal double Now;
        internal uint TickIndex;
        private short _nextId = 10000;

        internal Range(bool cull, bool parallel = false, bool profiling = false, bool capture = true, bool recordPositions = true)
        {
            _profiling = profiling;
            _capture = capture;
            _recordPositions = recordPositions;
            Server = new Srv { SpawnInitialMinersAtBoot = false, SimulationRandom = new Random(993) };
            Server.Boot(new ServerConfig
            {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
                Profiler = new() { Enabled = profiling, BroadcastBudget = profiling },
                Broadcast = new()
                {
                    MinUpdateHz = 2, ShipUpdateModel = "native", ParallelBuild = parallel,
                    MaxDegreeOfParallelism = 4, FireAudienceCull = cull,
                },
                Games = [new() { Name = "P993", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
                    SideNames = ["Alpha", "Bravo"] }],
            });
            Mission = Server.TestMission!;
            Mission.Random = new Random(993);
            Mission.Stage = MissionStage.Started;
            Sector = Server.TestSectors[0];
            Hull = Mission.Core.HullTypes[55];            // "Fighter" (PCore001), 3 fixed mounts
            DataPartTypeIGC weaponPart = default;
            foreach (short id in Hull.PreferredPartsTypes)
            {
                if (id == -1) break;
                if (Mission.Core.PartTypes.TryGetValue(id, out var candidate) && candidate.EquipmentType == EquipmentType.Weapon)
                {
                    weaponPart = candidate;
                    break;
                }
            }
            Weapon = weaponPart;
            Now = Mission.LastUpdate;
        }

        /// <summary>Add a ship; <paramref name="peer"/> false makes it a
        /// non-client target (a victim for the 213 lane).</summary>
        internal Ship AddShip(short side, Vector3 position, float yaw = 0f, bool peer = true, Sector? inSector = null)
        {
            var orientation = Orientation.Identity;
            if (yaw != 0f) orientation.Yaw(yaw);
            var sector = inSector ?? Sector;
            var ship = new Ship(_nextId++, Hull, position, Vector3.Zero, orientation) { SideId = side };
            Mission.PlaceShip(ship, sector);
            ship.MountWeapon((DataPartTypeIGC.Weapon)Weapon.Type, 0);
            Ships.Add(ship);
            if (peer) _pending.Add((new ConnectionId((uint)(700 + _pending.Count)), ship, sector));
            return ship;
        }

        internal void Start()
        {
            Server.TestResetSnapshotReplay();
            Server.TestSeedPeers(_pending);
            Peers.AddRange(_pending.Select(p => (p.Item1, p.Item2)));
            Server.TestUpdateCadenceClock = () => Now;
            if (_capture)
                Server.TestPublicationCapture = (peer, bytes, key, guaranteed) =>
                    Frames.Add(new(TickIndex, peer, key, guaranteed, bytes));
        }

        /// <summary>One real sim tick + the real broadcast suffix.</summary>
        internal void Tick(bool keepAlive = false)
        {
            Now += 1.0 / 20.0;
            if (keepAlive)
                foreach (var ship in Ships)
                {
                    ship.SetFraction(1f);
                    ship.Ammo = Hull.MaxAmmo;
                    ship.Energy = ship.EffectiveMaxEnergy();
                }
            Mission.Update(Now);
            if (_recordPositions)
                foreach (var ship in Ships) Positions.Add((TickIndex, ship));
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            Server.TestProjectRecordedTick(TickIndex, snapshot: true, measure: _profiling);
            Server.FlushBroadcastSnapshots();
            if (_profiling)
                TickWallMs.Add((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0
                    / System.Diagnostics.Stopwatch.Frequency);
            TickIndex++;
        }

        /// <summary>Fire exactly ONE round from <paramref name="shooter"/> (the
        /// ammo cap stops the burst) and return once it is on the wire.</summary>
        internal void FireSingle(Ship shooter)
        {
            shooter.StateM |= ShipControlState.AllWeapons;
            shooter.Ammo = 1;
            shooter.Energy = shooter.EffectiveMaxEnergy();
            for (int i = 0; i < 40; i++)
            {
                Tick();
                uint tick = TickIndex - 1;
                if (Frames.Any(f => f.Tick == tick && f.Peer.Value == 700 && Carries(f, ProjectileInstance.Fmid)))
                    return;
            }
            throw new InvalidOperationException("no PROJECTILE_INSTANCE(12) was relayed in 40 ticks");
        }

        internal void Advance(int ticks, bool keepAlive = false)
        {
            for (int i = 0; i < ticks; i++) Tick(keepAlive);
        }

        internal IEnumerable<Frame> To(Ship peer) => Frames.Where(f => f.Peer == ConnOf(peer));

        internal ConnectionId ConnOf(Ship ship) => Peers.First(p => ReferenceEquals(p.Ship, ship)).Conn;

        internal int Count(Ship peer, ushort fmid) => To(peer).Count(f => Carries(f, fmid));

        /// <summary>Frames about ONE subject ship (the emitter), by decoding the
        /// message's own subject id — so a test never mistakes another ship's
        /// status/pose frame for the fire dependency's.</summary>
        internal int Mark => Frames.Count;

        internal int DependencyFramesAbout(Ship peer, Ship subject, int fromIndex = 0)
        {
            int n = 0;
            foreach (var frame in Frames.Skip(fromIndex).Where(f => f.Peer == ConnOf(peer)))
                foreach (var message in Messages(frame))
                {
                    switch (FmidOf(message))
                    {
                        case ExportShip.Fmid when BitConverter.ToInt32(message, 8) == ExportShip.OtShip
                            && ExportShip.Decode(message).ShipId == subject.Id: n++; break;
                        case ShipStatusMessage.Fmid when ShipStatusMessage.Decode(message).ShipId == subject.Id: n++; break;
                        case SingleShipUpdate.Fmid when SingleShipUpdate.Decode(message).OidTarget == subject.Id: n++; break;
                    }
                }
            return n;
        }

        internal Vector3 PositionAt(uint tick, Ship ship)
            => Positions.Last(p => p.Tick <= tick && ReferenceEquals(p.Ship, ship)).Ship.Position;

        public void Dispose() => Server.Dispose();
    }

    // 5 900 u = the client's draw radius; the rule's margin adds the round's own
    // travel (this fixture's preferred gun: a 10/s gun, speed ≈ 1 000 u/s,
    // lifespan ≈ 1.5 s ⇒ ≈ 1 500 u) plus the viewer's own budget.
    private const float NearDistance = 1000f;       // well inside the radius
    private const float MarginDistance = 6200f;     // outside 5 900, inside 5 900 + this gun's 600 u of travel
    private const float FarDistance = 40000f;       // beyond every budget

    /// <summary>Today's audience (the flag OFF baseline): every peer in the
    /// shooter's sector gets the round AND the pose dependency, whether or not it
    /// could draw the round. This is also the OFF arm the flag-on tests are
    /// compared against.</summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Off_relays_the_round_and_its_pose_dependency_to_the_whole_sector()
    {
        Require.Artwork();
        using var range = new Range(cull: false);
        var shooter = range.AddShip(0, Vector3.Zero);
        var near = range.AddShip(1, new Vector3(0, 0, -NearDistance));
        var middle = range.AddShip(1, new Vector3(0, 0, -MarginDistance));
        var far = range.AddShip(1, new Vector3(0, 0, -FarDistance));
        range.Start();
        range.Tick();                    // one warm-up tick: the boot identity sweep is not the fire dependency
        int mark = range.Mark;
        range.FireSingle(shooter);

        foreach (var peer in new[] { near, middle, far })
        {
            Assert.True(range.Count(peer, ProjectileInstance.Fmid) >= 1,
                $"flag off: viewer {peer.Id} at {peer.Position} lost the round");
            Assert.True(range.DependencyFramesAbout(peer, shooter, mark) >= 3,
                $"flag off: viewer {peer.Id} lost the pose dependency (26+90+78)");
        }
        Assert.Contains("culled=0", range.Server.TakeFireAudienceReport());
    }

    /// <summary>The core rule, live: with the flag on a viewer outside the
    /// renderer's reach gets neither the round nor any dependency frame about the
    /// shooter; a viewer inside the radius and a viewer inside the MARGIN (round
    /// spawned outside 5 900 but flying at it) both still get everything.</summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void On_culls_a_far_viewer_with_its_dependency_and_keeps_the_near_and_margin_ones()
    {
        Require.Artwork();
        using var range = new Range(cull: true);
        var shooter = range.AddShip(0, Vector3.Zero);
        var near = range.AddShip(1, new Vector3(0, 0, -NearDistance));
        var middle = range.AddShip(1, new Vector3(0, 0, -MarginDistance));
        var far = range.AddShip(1, new Vector3(0, 0, -FarDistance));
        range.Start();
        range.Tick();                    // one warm-up tick: the boot identity sweep is not the fire dependency
        int mark = range.Mark;
        range.FireSingle(shooter);
        // The culled pair: no round and NO EnsureEmissionPose dependency.
        Assert.Equal(0, range.Count(far, ProjectileInstance.Fmid));
        Assert.Equal(0, range.DependencyFramesAbout(far, shooter, mark));

        // The kept pairs: the round AND the first-sight dependency (26 + 90 + 78).
        Assert.True(range.Count(near, ProjectileInstance.Fmid) >= 1, "a viewer inside 5 900 u must still get the round");
        Assert.True(range.DependencyFramesAbout(near, shooter, mark) >= 3,
            "a viewer inside 5 900 u must still get the pose dependency");
        Assert.True(range.Count(middle, ProjectileInstance.Fmid) >= 1,
            "a round spawned outside 5 900 u but flying at the viewer is drawn later — it must be kept");
        Assert.True(range.DependencyFramesAbout(middle, shooter, mark) >= 3,
            "the margin viewer (kept for the round) must keep its pose dependency too");

        string report = range.Server.TakeFireAudienceReport();
        // Exactly this one (shot, recipient) pair was culled; the shooter's own
        // peer, the near viewer and the margin viewer each got their first sight.
        Assert.Contains("culled=1", report);
        Assert.Contains("sent=3", report);
        Assert.Contains("initialPoses=3", report);
    }

    /// <summary>The shooter always gets its own round (own-echo de-dup and the bot
    /// own-round counter read its own copy), and channel 213 is untouched: its
    /// audience is a bot input stream (hit credit, attacker tracking) and must
    /// keep every sector viewer, far or near.</summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void On_keeps_the_shooter_and_leaves_the_213_audience_alone()
    {
        Require.Artwork();
        using var range = new Range(cull: true);
        var shooter = range.AddShip(0, Vector3.Zero);
        var victim = range.AddShip(1, new Vector3(0, 0, -600), peer: false);
        var far = range.AddShip(1, new Vector3(0, 0, -FarDistance));
        range.Start();
        range.FireSingle(shooter);
        range.Advance(40, keepAlive: true);            // let the round fly and land

        Assert.True(range.Count(shooter, ProjectileInstance.Fmid) >= 1,
            "the shooter's own peer must always receive its own round");
        Assert.True(range.Count(far, ProjectileInstance.Fmid) == 0, "the far viewer is still culled for 12");
        Assert.True(range.Count(far, ProjectileHullHit.Fmid) >= 1,
            "PROJECTILE_HULL_HIT(213) keeps its whole sector audience — bots consume it");
        Assert.True(victim.Fraction > 0f || victim.Dead || range.Count(shooter, ProjectileHullHit.Fmid) >= 1,
            "the fixture must actually land a hit for the 213 assertion to mean anything");
    }

    /// <summary>Docked and boarded viewers are decided by the same distance rule,
    /// because the renderer anchors the bolt test on the viewer's own ship pose
    /// whatever its state (<c>Game1.Rendering.cs</c>: <c>anchorPos = meUp.Position</c>
    /// is not gated on dock/parent state). A far docked/boarded viewer is culled; a
    /// near one is kept.</summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void On_decides_docked_and_boarded_viewers_by_the_same_radius()
    {
        Require.Artwork();
        using var range = new Range(cull: true);
        var shooter = range.AddShip(0, Vector3.Zero);
        var nearDocked = range.AddShip(1, new Vector3(0, 0, -NearDistance));
        var farDocked = range.AddShip(1, new Vector3(0, 0, -FarDistance));
        var nearGunner = range.AddShip(1, new Vector3(0, 0, -2 * NearDistance));
        var farGunner = range.AddShip(1, new Vector3(0, 0, -FarDistance));
        nearDocked.StationId = 3;                       // station view, same sector
        farDocked.StationId = 3;
        nearGunner.ParentShip = nearDocked;             // boarded gunner/observer child
        farGunner.ParentShip = farDocked;
        range.Start();
        range.FireSingle(shooter);

        Assert.True(range.Count(nearDocked, ProjectileInstance.Fmid) >= 1, "a docked viewer inside the radius still draws");
        Assert.True(range.Count(nearGunner, ProjectileInstance.Fmid) >= 1, "a boarded gunner inside the radius still draws");
        Assert.Equal(0, range.Count(farDocked, ProjectileInstance.Fmid));
        Assert.Equal(0, range.Count(farGunner, ProjectileInstance.Fmid));
    }

    /// <summary>Probe rounds go through <c>BroadcastProbeEmission</c> and obey the
    /// same radius (the deployer's own peer is kept like a shooter's).</summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void On_culls_probe_rounds_by_the_same_radius()
    {
        Require.Artwork();
        using var range = new Range(cull: true);
        var deployer = range.AddShip(0, Vector3.Zero);
        var near = range.AddShip(0, new Vector3(0, 0, -NearDistance));
        var far = range.AddShip(0, new Vector3(0, 0, -FarDistance));
        // An armed probe with a hostile target in reach (P491's Plas Gen 1 recipe,
        // probe id 30: quick-ready, c_eabmShootShips, dispersion 0).
        var probeType = range.Mission.Core.ProbeTypes[30];
        var probe = new Probe(20000, range.Sector, probeType, Vector3.Zero, range.Now, sideId: 0, deployer);
        range.Sector.Add(probe);
        // The probe's scanner reach is 270 u here, so the hostile sits inside it.
        range.AddShip(1, new Vector3(0, 0, -150f), peer: false);
        range.Start();
        // Tick past the probe's quick-ready delay (5 s at 20 Hz) plus its fire.
        for (int i = 0; i < 300; i++)
        {
            range.Tick(keepAlive: true);
            if (range.Frames.Any(f => Carries(f, ProjectileInstance.Fmid))) break;
        }
        Assert.True(probe.Ammo < probeType.Ammo, "the armed probe must have fired (fixture precondition)");
        Assert.True(range.Count(near, ProjectileInstance.Fmid) >= 1, "a near same-side viewer must get the probe's round");
        Assert.True(range.Count(deployer, ProjectileInstance.Fmid) >= 1, "the deployer keeps its own probe round");
        Assert.Equal(0, range.Count(far, ProjectileInstance.Fmid));
    }

    // ───────────────────── acceptance: no drawn pixel lost ─────────────────────

    /// <summary>THE acceptance test. A recorded spread fixture is replayed twice
    /// with the same seeded inputs; for every (viewer, round) the flag-off run
    /// delivered, the renderer's own rule (<see cref="ProjectileVisibility"/>,
    /// applied to the round's real wire position advanced along its wire velocity,
    /// against that viewer's own recorded ship position) decides whether at least
    /// one frame of the round's life would have been DRAWN. Every drawn round must
    /// still be delivered with the flag on, byte-identically.</summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Every_round_the_renderer_would_draw_is_still_delivered_with_the_flag_on()
    {
        Require.Artwork();
        const int ticks = 40;
        var (offRounds, positions) = SpreadFixture(cull: false, ticks);
        var (onRounds, onPositions) = SpreadFixture(cull: true, ticks);

        Assert.True(offRounds.Count > 100, $"the fixture must deliver plenty of rounds (got {offRounds.Count})");
        Assert.True(onRounds.Count < offRounds.Count, "the flag-on run must cull something (spread fixture)");
        // The same seeded world: the kept frames are the same bytes.
        static string Key(ConnectionId peer, uint tick, byte[] payload) => $"{peer.Value}:{tick}:{Convert.ToHexString(payload)}";
        var offKeys = offRounds.Select(o => Key(o.Peer, o.Tick, o.Payload)).ToHashSet();
        var kept = onRounds.Select(o => Key(o.Peer, o.Tick, o.Payload)).ToHashSet();
        Assert.True(offKeys.IsSupersetOf(kept), "the flag-on run published a frame the flag-off run did not");

        int drawn = 0, culledAndDrawn = 0;
        foreach (var round in offRounds)
        {
            bool drawable = false;
            var spawn = round.Position;
            for (int step = 0; step <= 40 && !drawable; step++)
            {
                float t = round.Lifespan * step / 40f;
                var pos = spawn + round.Velocity * t;
                var viewerPos = positions[(round.Peer, round.Tick + (uint)(t * 20f) <= (uint)(ticks - 1)
                    ? round.Tick + (uint)(t * 20f) : (uint)(ticks - 1))];
                if ((pos - viewerPos).LengthSquared() <= ProjectileVisibility.VisibilityRadiusSqr) drawable = true;
            }
            if (!drawable) continue;
            drawn++;
            if (!kept.Contains(Key(round.Peer, round.Tick, round.Payload))) culledAndDrawn++;
        }
        Assert.True(drawn > 0, "the acceptance fixture drew nothing — it proves nothing");
        Assert.Equal(0, culledAndDrawn);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[p993-acceptance] ticks={ticks} offRounds={offRounds.Count} onRounds={onRounds.Count} " +
            $"drawnable={drawn} culledDrawn={culledAndDrawn} positions={positions.Count} onPositions={onPositions.Count}"));
    }

    private sealed record Round(ConnectionId Peer, uint Tick, byte[] Payload, Vector3 Position, Vector3 Velocity, float Lifespan);

    /// <summary>The acceptance fixture: 12 peers spread over an 11 000 u battle
    /// line (so roughly a third of the deliveries are beyond the draw radius, the
    /// P992 spread shape), four of them firing 10 rounds/s for
    /// <paramref name="ticks"/> ticks, every viewer's own ship position recorded
    /// per tick.</summary>
    /// <summary>Returns the rounds the arm delivered and every viewer's own ship
    /// position per tick (the acceptance test's draw anchor).</summary>
    private static (List<Round> Rounds, Dictionary<(ConnectionId, uint), Vector3> Positions)
        SpreadFixture(bool cull, int ticks)
    {
        using var range = new Range(cull: cull);
        var shooters = new List<Ship>();
        // ~500 u apart along z, 2 000 u apart along a 10 000 u line: the P992
        // spread shape (a third of the deliveries are beyond the 5 900 u radius).
        for (int i = 0; i < 6; i++) shooters.Add(range.AddShip(0, new Vector3(50000f + i * 2000f, 0, 300f)));
        for (int i = 0; i < 6; i++) range.AddShip(1, new Vector3(50000f + i * 2000f + 1000f, 0, -300f), yaw: MathF.PI);
        range.Start();
        foreach (var shooter in shooters) shooter.StateM = ShipControlState.AllWeapons;
        range.Advance(ticks, keepAlive: true);

        var rounds = new List<Round>();
        foreach (var frame in range.Frames)
            foreach (var message in Messages(frame))
            {
                if (FmidOf(message) != ProjectileInstance.Fmid) continue;
                var round = ProjectileInstance.Decode(message);
                rounds.Add(new Round(frame.Peer, frame.Tick, message, round.Position, round.Velocity, round.Lifespan));
            }
        var positions = new Dictionary<(ConnectionId, uint), Vector3>();
        foreach (var (tick, ship) in range.Positions)
        {
            var conn = range.Peers.FirstOrDefault(p => ReferenceEquals(p.Ship, ship)).Conn;
            if (conn.Value != 0) positions[(conn, tick)] = ship.Position;
        }
        return (rounds, positions);
    }

    // ───────────────────── measurement (report §3) ─────────────────────

    /// <summary>P992 §3.1's fixture shape (411 peers, 3 sectors, 137 per sector,
    /// 36 shooters, 100 ticks = 5 s of a real sim + the real broadcast suffix, the
    /// P990/P983 telemetry bucket pinned) with <c>broadcast.fireAudienceCull</c>
    /// off and on, in the SPREAD (150 u spacing, a ~10 000 u battle line) and
    /// PACKED (40 u) shapes. Prints one row per arm; the report quotes them. The
    /// identities asserted here are the ones the report's numbers rest on: every
    /// shot is one 58-byte frame per KEPT recipient, `213` is untouched, and the
    /// dependency follows the kept audience.</summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Loopback_showcase_measures_the_cull_spread_and_packed()
    {
        Require.Artwork();
        static int Env(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : fallback;
        int peers = Env("P993_PEERS", 411);
        int ticks = Env("P993_TICKS", 100);
        int sectors = Env("P993_SECTORS", 3);
        int firing = Env("P993_FIRING", 36);
        // Every arm runs twice and the SECOND (warm) run is reported: the first
        // pays the pipeline's JIT cost, which would otherwise land on whichever
        // arm happened to run first.
        Measure Arm(float spacing, int firingShips, bool cull)
        {
            MeasureTraffic(peers, firingShips, ticks, sectors, spacing, cull);   // warm-up
            return MeasureTraffic(peers, firingShips, ticks, sectors, spacing, cull);
        }
        var offSpread = Arm(150f, firing, false);
        var onSpread = Arm(150f, firing, true);
        var offPacked = Arm(40f, firing, false);
        var onPacked = Arm(40f, firing, true);
        var quietSpread = Arm(150f, 0, false);
        foreach (var row in new[] { offSpread, onSpread, offPacked, onPacked, quietSpread })
            Console.WriteLine(row.Line);

        // The cull only ever removes 12 deliveries (and the dependency they drag
        // in); 213 keeps its whole audience in every arm, packed or spread.
        Assert.Equal(offSpread.Msgs213, onSpread.Msgs213);
        Assert.Equal(offPacked.Msgs213, onPacked.Msgs213);
        Assert.Equal(onSpread.Msgs213, onPacked.Msgs213);
        Assert.True(onSpread.Msgs12 < offSpread.Msgs12,
            $"the spread fixture must cull 12 frames (off {offSpread.Msgs12}, on {onSpread.Msgs12})");
        Assert.True(onSpread.Bytes12 < offSpread.Bytes12);
        // A packed knife-fight is already inside the radius: nothing to cull.
        Assert.Equal(offPacked.Msgs12, onPacked.Msgs12);
        Assert.Equal(offPacked.Bytes12, onPacked.Bytes12);
        // The report's stock arm is P992's measured shape, to the byte.
        Assert.Equal(1764, offSpread.Shots);
        Assert.Equal(offSpread.Shots * (peers / sectors), offSpread.Msgs12);
        Assert.Equal(offSpread.Msgs12 * (ProjectileInstance.FixedSize + FedPacket.CrcWidth), offSpread.Bytes12);

        // The dependency is isolated the way P992 isolated it: the firing arm minus
        // the identical quiet arm, per id (the raw 26/90 totals are dominated by the
        // world's own export sweeps, which the cull never touches).
        foreach (var (label, off, on) in new[] { ("spread", offSpread, onSpread), ("packed", offPacked, onPacked) })
        {
            long dep78Off = off.Bytes78 - quietSpread.Bytes78, dep78On = on.Bytes78 - quietSpread.Bytes78;
            long dep26Off = off.Bytes26 - quietSpread.Bytes26, dep26On = on.Bytes26 - quietSpread.Bytes26;
            long dep90Off = off.Bytes90 - quietSpread.Bytes90, dep90On = on.Bytes90 - quietSpread.Bytes90;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[p993-dep] {label} off78={dep78Off} on78={dep78On} off26={dep26Off} on26={dep26On} " +
                $"off90={dep90Off} on90={dep90On} " +
                $"offDep={dep78Off + dep26Off + dep90Off} onDep={dep78On + dep26On + dep90On} " +
                $"culled={on.FireAudienceReport.Split("culled=")[^1]} " +
                $"b12SavePct={100.0 * (off.Bytes12 - on.Bytes12) / off.Bytes12:0.00} " +
                $"egressSavePct={100.0 * (off.TotalBytes - on.TotalBytes) / off.TotalBytes:0.00}"));
            if (label == "spread")
            {
                Assert.True(dep78On < dep78Off, $"the culled pairs must stop the 78 refresh (off {dep78Off}, on {dep78On})");
                Assert.True(dep26On <= dep26Off);
                Assert.True(dep90On <= dep90Off);
            }
        }
    }

    /// <summary>Linear-interpolated p95 of a sample list (0 when empty).</summary>
    private static double Percentile(List<double> samples, double q)
    {
        if (samples.Count == 0) return 0;
        var sorted = samples.OrderBy(v => v).ToArray();
        double pos = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(sorted.Length - 1, lo + 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    private sealed record Measure(
        int Peers, int Firing, int Ticks, int Sectors, float Spacing, bool Cull,
        long Shots, long Hits, long ShotPairs, long ShotPairsNear, long HitPairs,
        long Msgs12, long Bytes12, long Msgs213, long Bytes213,
        long Msgs78, long Bytes78, long Msgs26, long Bytes26, long Msgs90, long Bytes90,
        long TotalBytes, long HeavyBytes, double WallSeconds,
        double TickP95Ms, double BroadcastShipsUpdateMeanMs, double BroadcastShipsUpdateP95Ms,
        string FireAudienceReport)
    {
        internal long DependencyBytes => Bytes78 + Bytes26 + Bytes90;
        internal string Line => string.Create(CultureInfo.InvariantCulture,
            $"[p993] cull={(Cull ? 1 : 0)} spacing={Spacing:0} peers={Peers} firing={Firing} ticks={Ticks} sectors={Sectors} " +
            $"shots={Shots} hits={Hits} near5900={ShotPairsNear}/{ShotPairs} hnear={HitPairs} " +
            $"m12={Msgs12} b12={Bytes12} m213={Msgs213} b213={Bytes213} " +
            $"m78={Msgs78} b78={Bytes78} m26={Msgs26} b26={Bytes26} m90={Msgs90} b90={Bytes90} " +
            $"dep={DependencyBytes} total={TotalBytes} heavy={HeavyBytes} wallS={WallSeconds:0.00} " +
            $"tickP95={TickP95Ms:0.000} bsuMean={BroadcastShipsUpdateMeanMs:0.000} bsuP95={BroadcastShipsUpdateP95Ms:0.000} " +
            $"fire=\"{FireAudienceReport}\"");
    }

    private static Measure MeasureTraffic(int peers, int firing, int ticks, int sectors, float spacing, bool cull)
    {
        using var range = new Range(cull: cull, parallel: true, profiling: true, capture: false, recordPositions: false);
        var ships = new List<Ship>();
        var shooters = new List<Ship>();
        for (int i = 0; i < peers; i++)
        {
            short side = (short)((i / sectors) % 2);
            int sec = i % sectors;
            int rank = i / (2 * sectors);
            var ship = range.AddShip(side, new Vector3(50000f + sec * 8000f + rank * spacing, 0, side == 0 ? 300f : -300f),
                yaw: side == 0 ? 0f : MathF.PI, inSector: range.Server.TestSectors[sec]);
            ships.Add(ship);
            if (i < firing) shooters.Add(ship);
        }
        range.Start();

        // How many (shot, recipient) deliveries are inside the client's own draw
        // radius of the round's spawn point — the P992 §3.9 measurement.
        long shotPairs = 0, shotPairsNear = 0, hitPairs = 0;
        if (range.Mission.Site is ServerHost host)
        {
            host.ProjectileCreated += p =>
            {
                foreach (var ship in ships)
                {
                    if (!ReferenceEquals(ship.Sector, p.OwnerSector)) continue;
                    shotPairs++;
                    if ((ship.Position - p.Position).LengthSquared() <= ProjectileVisibility.VisibilityRadiusSqr) shotPairsNear++;
                }
            };
            host.ProjectileHit += (p, impact, _, _, _, _, _) =>
            {
                foreach (var ship in ships)
                    if (ReferenceEquals(ship.Sector, p.OwnerSector)) hitPairs++;
            };
        }
        var telemetry = range.Server.BroadcastBudgetTelemetry!;
        telemetry.SetSecond(1000);                     // one pinned bucket: whole-window exact totals
        foreach (var shooter in shooters) shooter.StateM |= ShipControlState.AllWeapons;
        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        range.Advance(ticks, keepAlive: true);
        wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency - wall;

        long Msgs(ushort fmid) => telemetry.OrderedLaneWindowMessages(fmid, 1000);
        long Bytes(ushort fmid) => telemetry.OrderedLaneWindowBytes(fmid, 1000);
        var snap = TickProfiler.Snapshot();
        double p95(string bucket) => snap.TryGetValue(bucket, out var s) ? s.P95Ms : 0;
        double mean(string bucket) => snap.TryGetValue(bucket, out var s) ? s.MeanMs : 0;
        double tickP95 = Percentile(range.TickWallMs, 0.95);
        long hits = Msgs(ProjectileHullHit.Fmid) / Math.Max(1, peers / Math.Max(1, sectors));
        long shots = range.Mission.Sides.Sum(s => range.Mission.Skills!.Count(s.Id, ShowcaseSkill.Guns));
        string fire = range.Server.TakeFireAudienceReport();
        return new Measure(peers, firing, ticks, sectors, spacing, cull, shots, hits, shotPairs, shotPairsNear, hitPairs,
            Msgs(ProjectileInstance.Fmid), Bytes(ProjectileInstance.Fmid),
            Msgs(ProjectileHullHit.Fmid), Bytes(ProjectileHullHit.Fmid),
            Msgs(SingleShipUpdate.Fmid), Bytes(SingleShipUpdate.Fmid),
            Msgs(ExportShip.Fmid), Bytes(ExportShip.Fmid),
            Msgs(ShipStatusMessage.Fmid), Bytes(ShipStatusMessage.Fmid),
            telemetry.WindowTotal(1000), telemetry.WindowBytes(BroadcastCategory.HeavyShipUpdates, 1000),
            wall, tickP95, mean("step:broadcastShipsUpdate"), p95("step:broadcastShipsUpdate"), fire);
    }
}
