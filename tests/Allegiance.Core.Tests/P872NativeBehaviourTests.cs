using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Core.Transport;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

using Server = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

/// <summary>
/// P872 — server-side native behaviour from the n/a audit: the drone-chatter
/// text producers (SIM-0258/0259/0261/0269/0270, native
/// <c>Igc/shipIGC.cpp:2373-2407,2682-2705</c> and
/// <c>Igc/shipIGC.h:2384-2414,2436-2451</c>), the 500-element explosion
/// recipient cutoff (SIM-0068, <c>Igc/clusterIGC.cpp:365-481</c>), the
/// replay-count/context-name pair (SIM-0750/0751,
/// <c>Igc/missionigc.cpp:3849-3852,4211-4238</c>), and the empty
/// player-created-session reap decision (SIM-0146,
/// <c>FedSrv/FedSrv.CPP:7871-7910</c> with Domingo's 2026-10-28 lifecycle
/// ruling: configured/admin worlds persist, player-created sessions are reaped
/// after ten seconds empty).
/// </summary>
public sealed class P872NativeBehaviourTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // ── domain harness (verbatim shape from P708ShipUpdateTests) ────────────

    private static Mission Domain(IIgcSite? site = null, int sideCount = 2)
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var clusters = Enumerable.Range(0, 3).Select(i => DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = (short)i }).ToArray();
        var civ = core.Civilizations.Keys.First();
        var m = Mission.Assemble(core, MissionMap.FromLists(clusters, [], [], []),
            Enumerable.Range(0, sideCount).Select(i => new SideSpec((short)i, $"Side {i}", civ)).ToArray(), site ?? new NullIgcSite());
        m.Stage = MissionStage.Started;
        foreach (var side in m.Sides) side.GlobalAttributes = GlobalAttributeSet.Identity;
        return m;
    }

    private static Ship Place(Mission m, short id = 100, short side = 0, short sector = 0, float x = 0, float z = 0, byte pilot = Ship.PilotTypeWingman, bool npc = true)
    {
        var hull = m.Core.HullTypes.Values.First() with
        {
            Mass = 100, Thrust = 0, Speed = 1000, MaxEnergy = 1000, MaxAmmo = 1000, RechargeRate = 0,
            HitPoints = 1000, Length = 10, HabmCapabilities = 0, RipcordSpeed = 1,
            MaxFixedWeapons = 1, MaxWeapons = 1, CapacityDispenser = 1000,
            Hardpoints = [new(0, 0, "", "", short.MaxValue, true)], ScannerRange = 10000, Signature = 1
        };
        var s = new Ship(id, hull, new(x, 0, z), Vector3.Zero, Orientation.Identity)
            { SideId = side, IsNpc = npc, PilotType = pilot };
        m.PlaceShip(s, sector); return s;
    }

    private static DataPartTypeIGC Part(DataPartTypeIGC.Subtype subtype) =>
        new(0, 0, "", "", "Test nan", "", 0, [], [], 0, 0, 32000, -1, 0, "", subtype);

    private static MountedPart Gun(Ship ship, float power = 10, byte mounted = 240)
    {
        var core = ship.Mission!.Core;
        var projectile = core.ProjectileTypes.Values.First() with { Speed = 100, Lifespan = 10, Power = power, AbsoluteF = true };
        ((IDictionary<short, DataProjectileTypeIGC>)core.ProjectileTypes)[projectile.ProjectileTypeId] = projectile;
        return ship.MountPart(Part(new DataPartTypeIGC.Weapon(0, 0.1f, 0, 0, 0, projectile.ProjectileTypeId, 0, 0, 0)), 1, 0, mounted)!;
    }

    private static void Call(Ship ship, string method, params object[] args) =>
        typeof(Ship).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ship, args);

    private static void Call(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    // ── SIM-0258/0259/0261 — the defend chatter text producers ──────────────

    /// <summary>P872/SIM-0261 — the escorting acquire emits native's exact text
    /// <c>Attacking %s (%f) to defend %s*</c> (shipIGC.cpp:2692-2694): model
    /// NAMES (not ids), the scan distance in six-decimal native %f form, and
    /// the trailing star; sound 1337 (droneInTransitSound).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Defend_acquire_chat_uses_native_text_names_distance_and_star()
    {
        Require.Artwork(); var site = new RecordingIgcSite(); var m = Domain(site);
        var s = Place(m, 100, 0, x: 200); s.Name = "Winger"; Gun(s);
        var charge = Place(m, 101, 0); charge.Name = "Charge";       // scan centre
        var threat = Place(m, 102, 1, z: -1200); threat.Name = "Threat";
        threat.SideVisibility.SetSideVisibility(0, true);
        s.DefendTarget = charge;

        Call(s, "UpdateWingmanDefend");

        Assert.Same(threat, s.AttackTarget);
        var call = Assert.Single(site.Calls, c => c.StartsWith("DroneChatEvent("));
        Assert.Contains("1337", call);
        Assert.Contains("\"Attacking Threat (1200.000000) to defend Charge*\"", call);
    }

    /// <summary>P872/SIM-0261 — the acquire branch is gated on a mounted,
    /// damaging primary weapon (shipIGC.cpp:2683); an unarmed guard neither
    /// acquires nor chats.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unarmed_guard_neither_acquires_nor_chats_on_defend_acquire()
    {
        Require.Artwork(); var site = new RecordingIgcSite(); var m = Domain(site);
        var s = Place(m, 100, 0); s.Name = "Winger";           // NO weapon mounted
        var charge = Place(m, 101, 0, x: 100); charge.Name = "Charge";
        var threat = Place(m, 102, 1, z: -1200); threat.Name = "Threat";
        threat.SideVisibility.SetSideVisibility(0, true);
        s.DefendTarget = charge;

        Call(s, "UpdateWingmanDefend");

        Assert.Null(s.AttackTarget);
        Assert.DoesNotContain(site.Calls, c => c.StartsWith("DroneChatEvent("));
    }

    /// <summary>P872/SIM-0258 — the >2200 retarget emits native's exact text
    /// <c>Attacking %s (%fm) to defend %s</c> (shipIGC.cpp:2386-2388) when the
    /// nearer threat's scan distance beats <c>distToProtectee − 1000</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Defend_retarget_chat_uses_native_text_with_metre_distance()
    {
        Require.Artwork(); var site = new RecordingIgcSite(); var m = Domain(site);
        var s = Place(m, 100, 0); s.Name = "Winger"; Gun(s);
        var charge = Place(m, 101, 0, z: 0); charge.Name = "Charge";
        var far = Place(m, 102, 1, z: -3000); far.Name = "Far";
        var closer = Place(m, 103, 1, z: -1000); closer.Name = "Closer";
        far.SideVisibility.SetSideVisibility(0, true);
        closer.SideVisibility.SetSideVisibility(0, true);
        s.DefendTarget = charge;
        s.AttackTarget = far;                                  // plan slot; distToProtectee = 3000 (>2200, ≤3500)

        Call(s, "UpdateWingmanDefend");

        Assert.Same(closer, s.AttackTarget);
        var call = Assert.Single(site.Calls, c => c.StartsWith("DroneChatEvent("));
        Assert.Contains("1337", call);
        Assert.Contains("\"Attacking Closer (1000.000000m) to defend Charge\"", call);
    }

    /// <summary>P872/SIM-0259 — the return-to-protectee disengage emits
    /// native's exact text <c>Returning to defend %s</c> with the protectee's
    /// model name (shipIGC.cpp:2401-2403), once per transition.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Defend_return_chat_names_the_protectee()
    {
        Require.Artwork(); var site = new RecordingIgcSite(); var m = Domain(site);
        var s = Place(m, 100, 0); s.Name = "Winger"; Gun(s);
        var charge = Place(m, 101, 0, z: 4000); charge.Name = "Charge";
        var near = Place(m, 102, 1, z: -100); near.Name = "Near";
        near.SideVisibility.SetSideVisibility(0, true);
        s.DefendTarget = charge;
        s.AttackTarget = near;                                 // plan slot; distToProtectee ≈ 4100 > 3500

        Call(s, "UpdateWingmanDefend");

        Assert.Null(s.AttackTarget);
        var call = Assert.Single(site.Calls, c => c.StartsWith("DroneChatEvent("));
        Assert.Contains("1337", call);
        Assert.Contains("\"Returning to defend Charge\"", call);
    }

    // ── SIM-0270 — the wingman default attack caption ───────────────────────

    /// <summary>P872/SIM-0270 — the default wingman attack emits
    /// <c>Attacking %s</c> with the target's model name (shipIGC.h:2448-2450)
    /// after the accepted default attack, only when the host override returns
    /// false and a target exists.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Default_wingman_attack_chats_the_named_target()
    {
        Require.Artwork(); var site = new RecordingIgcSite(); var m = Domain(site);
        var s = Place(m, 100, 0); s.Name = "Winger"; Gun(s);
        var enemy = Place(m, 101, 1, z: -500); enemy.Name = "Enemy";
        enemy.SideVisibility.SetSideVisibility(0, true);

        Call(s, "DecideWingmanDefaultOrder");

        Assert.Same(enemy, s.AttackTarget);
        var call = Assert.Single(site.Calls, c => c.StartsWith("DroneChatEvent("));
        Assert.Contains("1337", call);
        Assert.Contains("\"Attacking Enemy\"", call);
    }

    /// <summary>P872/SIM-0270 — a host that supplies the default order
    /// (HandlePickDefaultOrder true) suppresses both acquisition and chat.</summary>
    private sealed class OverrideSite : IIgcSite
    {
        public List<string> Calls { get; } = new();
        public bool HandlePickDefaultOrder(IShipIgc pship) => true;
        public void DroneChatEvent(Ship ship, short sideId, short soundId, string text) =>
            Calls.Add($"DroneChatEvent({soundId}, \"{text}\")");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Host_default_order_override_suppresses_the_attack_chat()
    {
        Require.Artwork(); var host = new OverrideSite(); var m = Domain(host);
        var s = Place(m, 100, 0); s.Name = "Winger"; Gun(s);
        Place(m, 101, 1, z: -500).Name = "Enemy";

        Call(s, "DecideWingmanDefaultOrder");

        Assert.Null(s.AttackTarget);
        Assert.Empty(host.Calls);
    }

    // ── SIM-0269 — the builder default-order chatter ────────────────────────

    private static DataAsteroidIGC Rock(short id, Vector3 pos, short sectorId) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: 0f, OreMax: 0f,
        AabmCapabilities: DataAsteroidIGC.AabmBuildable,
        AsteroidId: id, HitPoints: 0f, Radius: 100,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    /// <summary>P872/SIM-0269 — the builder default order emits
    /// <c>Building %s at %s</c> — the station TYPE name at the asteroid's model
    /// name — only for an accepted build, not a goto (shipIGC.h:2404-2410);
    /// bucket-spawned constructors (the native goto rewrite) never emit it.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Builder_default_order_chats_building_type_at_named_rock()
    {
        Require.Artwork(); var site = new RecordingIgcSite();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var clusters = Enumerable.Range(0, 2).Select(i => DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = (short)i }).ToArray();
        var rock = Rock(7000, new Vector3(0, 0, 0), 0);
        var civ = core.Civilizations.Keys.First();
        var m = Mission.Assemble(core, MissionMap.FromLists(clusters, [], [], [rock]),
            Enumerable.Range(0, 2).Select(i => new SideSpec((short)i, $"Side {i}", civ)).ToArray(), site);
        m.Stage = MissionStage.Started;
        foreach (var side in m.Sides) side.GlobalAttributes = GlobalAttributeSet.Identity;

        var builder = Place(m, 100, 0, pilot: Ship.PilotTypeBuilder); builder.Name = "Constructor7";
        var stationType = m.Core.StationTypes.Values.First();
        builder.BuilderStationType = stationType;
        var sector = m.Sectors[0];

        Call(builder, "DecideBuilderDefaultOrder");

        var call = Assert.Single(site.Calls, c => c.StartsWith("DroneChatEvent("));
        Assert.Contains("1337", call);
        Assert.Contains($"\"Building {stationType.Name} at ROID_7000\"", call);
        GC.KeepAlive(sector);
    }

    /// <summary>P872/SIM-0269 — a bucket-spawned constructor awaiting a Build
    /// order (native's c_cidGoto rewrite, FedSrv.CPP:11358-11366) never emits
    /// the Building chatter.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Bucket_constructor_awaiting_build_order_does_not_chat_building()
    {
        Require.Artwork(); var site = new RecordingIgcSite();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var clusters = Enumerable.Range(0, 2).Select(i => DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = (short)i }).ToArray();
        var rock = Rock(7001, new Vector3(0, 0, 0), 0);
        var civ = core.Civilizations.Keys.First();
        var m = Mission.Assemble(core, MissionMap.FromLists(clusters, [], [], [rock]),
            Enumerable.Range(0, 2).Select(i => new SideSpec((short)i, $"Side {i}", civ)).ToArray(), site);
        m.Stage = MissionStage.Started;
        foreach (var side in m.Sides) side.GlobalAttributes = GlobalAttributeSet.Identity;

        var builder = Place(m, 100, 0, pilot: Ship.PilotTypeBuilder);
        builder.BuilderStationType = m.Core.StationTypes.Values.First();
        builder.WaitsForBuildOrder = true;                     // the goto branch

        Call(builder, "DecideBuilderDefaultOrder");

        Assert.DoesNotContain(site.Calls, c => c.StartsWith("DroneChatEvent("));
        Assert.Null(builder.BuilderAsteroidTarget);
    }

    // ── SIM-0068 — the 500-element explosion recipient cutoff ───────────────

    private static (DataMineTypeIGC Mine, DataHullTypeIGC Hull) MineCombo()
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var mine = core.MineTypes.Values.Where(mm => mm.Power > 0f && mm.Endurance > 0f).OrderBy(mm => mm.Power).First();
        return (mine, core.HullTypes.Values.First());
    }

    /// <summary>P872/SIM-0068 — the explosion recipient pass caches the FIRST
    /// 500 eligible candidates in insertion order (clusterIGC.cpp:368-400);
    /// with 505 mines in one sector, exactly the first 500 take ReduceStrength
    /// and the five beyond the cutoff are untouched by any blast in the batch.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Explosion_recipients_are_capped_at_500_in_insertion_order()
    {
        Require.Artwork();
        var (mineType, hull) = MineCombo();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var clusters = new[] { DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 } };
        var civ = core.Civilizations.Keys.First();
        var m = Mission.Assemble(core, MissionMap.FromLists(clusters, [], [], []),
            [new SideSpec(0, "Team", civ)], new NullIgcSite());
        m.Stage = MissionStage.Started;
        var sector = m.Sectors[0];
        short id = 2000;
        var holder = Place(m, id++, 0);                        // launcher, holds nothing
        var mines = new List<Mine>();
        for (int i = 0; i < 505; i++)
        {
            var mine = new Mine(id++, sector, mineType, new Vector3(0, 0, 0), m.LastUpdate, holder);
            sector.Add(mine);
            mines.Add(mine);
        }

        var explosion = new Explosion(mineType.DamageType, mineType.Power, 100000f, m.LastUpdate, Vector3.Zero, Launcher: null);
        sector.ResolveExplosion(explosion, m.LastUpdate);

        float reduced = MathF.Exp(-mineType.Power / mineType.Endurance);
        for (int i = 0; i < 500; i++)
            Assert.Equal(reduced, mines[i].Fraction, 4);       // first 500, insertion order
        for (int i = 500; i < 505; i++)
            Assert.Equal(1f, mines[i].Fraction, 4);            // beyond the cutoff: untouched
    }

    // ── SIM-0750/0751 — replay count and context name ───────────────────────

    /// <summary>P872/SIM-0750 — native ResetMission increments the replay count
    /// only after the first generation and clears the context-name cache
    /// (missionigc.cpp:3849-3857); the first BeginReplay just marks generated.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Replay_count_increments_after_first_generation_and_invalidates_context()
    {
        Require.Artwork(); var m = Domain();
        Assert.False(m.HasGenerated);
        Assert.Equal(0, m.ReplayCount);
        string first = m.GetContextName();
        m.BeginReplay();                                       // first reset: no increment (m_bHasGenerated was false)
        Assert.Equal(0, m.ReplayCount);
        Assert.True(m.HasGenerated);
        m.BeginReplay();                                       // second reset: replay #1, context invalidated
        Assert.Equal(1, m.ReplayCount);
        Assert.NotEqual(first, m.GetContextName());
        m.BeginReplay();
        Assert.Equal(2, m.ReplayCount);
    }

    /// <summary>P872/SIM-0751 — the context name is the cached
    /// <c>%c%02d%02d%02d%02d%02d%04d%02d</c> string: U/Z by admin-club,
    /// creation wall-time MMDDHHmmss, mission id %10000, replay %100
    /// (missionigc.cpp:4225-4235); the cache survives unchanged calls.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Context_name_is_the_cached_UZ_timestamp_mission_and_replay_string()
    {
        Require.Artwork(); var m = Domain();
        m.CreatedWallTime = new DateTimeOffset(2031, 2, 3, 4, 5, 6, TimeSpan.Zero);
        m.MissionId = 12345;                                   // %10000 → 2345
        m.ReplayCount = 7;

        string context = m.GetContextName();
        Assert.Equal("U0203040506234507", context);
        Assert.Same(context, m.GetContextName());              // cached, not regenerated

        m.BeginReplay(); m.BeginReplay();                      // replay #1 invalidates
        Assert.Equal("U0203040506234508", m.GetContextName());
        // The Z prefix belongs to admin club games, decided at generation;
        // native caches ONE name (m_strContextName), so a separate mission
        // with the same stamps renders Z.
        var m2 = Domain();
        m2.CreatedWallTime = m.CreatedWallTime; m2.MissionId = m.MissionId; m2.ReplayCount = m.ReplayCount;
        Assert.Equal("Z0203040506234508", m2.GetContextName(adminClubGame: true));
    }
}

// ══ SIM-0146 — the empty player-created-session reap (live server) ══════════

[Collection("LiveNetwork")]
public sealed class P872SessionReapTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static void Call(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P872 Reap", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    /// <summary>P872/SIM-0146 — a player-created session nobody joined is
    /// deleted once it has existed more than ten seconds (FedSrv.CPP:7871-7910,
    /// one per check); configured worlds persist per Domingo's 2026-10-28
    /// ruling, as do still-young or occupied player-created sessions.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Empty_player_created_session_is_reaped_after_ten_seconds_but_configured_worlds_persist()
    {
        Require.Artwork();
        var server = new Server();
        server.Boot(Config());
        using var _server = server;
        var mission = server.TestMission!;
        Assert.False(mission.PlayerCreated);                   // boot worlds are configured

        // A configured world survives even an ancient empty age.
        mission.CreatedAt = -1000;
        Call(server, "AdvancePeriodicRules", 1000.0);
        Assert.Same(mission, server.TestMission);

        // A player-created session inside the ten-second window persists.
        mission.PlayerCreated = true;
        mission.CreatedAt = 995.0;
        Call(server, "AdvancePeriodicRules", 1000.0);
        Assert.Same(mission, server.TestMission);

        // Past ten seconds empty: reaped (one mission per check).
        Call(server, "AdvancePeriodicRules", 1005.5);
        Assert.Null(server.TestMission);
    }

    /// <summary>P872/SIM-0146 — a player-created session someone joined is
    /// never reaped, whatever its age (GetCountOfPlayers == 0 gate,
    /// FedSrv.CPP:7885).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void An_occupied_player_created_session_is_never_reaped()
    {
        Require.Artwork();
        var server = new Server();
        server.Boot(Config());
        using var _server = server;
        var mission = server.TestMission!;
        mission.PlayerCreated = true;
        mission.CreatedAt = 0.0;
        var ship = new Ship(17000, mission.Core.HullTypes.Values.First(),
            Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, mission.Sectors[0]);
        var owner = new ConnectionId(950);
        server.TestSeedPeers([(owner, ship, mission.Sectors[0])]);

        Call(server, "AdvancePeriodicRules", 100000.0);

        Assert.Same(mission, server.TestMission);
    }
}
