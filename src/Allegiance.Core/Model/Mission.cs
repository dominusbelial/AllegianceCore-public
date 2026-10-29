namespace Allegiance.Core.Model;

using System.Globalization;
using Allegiance.Core.Collision;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;

/// <summary>
/// A <b>live, static mission object graph</b> assembled from a
/// <see cref="StaticCore"/> and a <see cref="MissionMap"/>: one <see cref="Sector"/>
/// per <c>OT_cluster</c>, each with its warps (destinations resolved across
/// sectors), stations and asteroids, plus the caller-supplied <see cref="Side"/>s.
/// </summary>
/// <remarks>
/// This is <b>pure data assembly</b> — the portable half of the reference
/// mission build (see <c>MISSION_INIT.md</c> §5). The runtime half — the
/// <see cref="IIgcSite"/> seam (<see cref="Site"/>) and the tick — is bound
/// here but not driven: there is no tick, no network, no mutable
/// ship/physics state. Side skeletons
/// are taken from the caller (<see cref="SideSpec"/>) because the reference
/// engine derives them from <c>MissionParams</c> + host-supplied names, which
/// the two documents do not carry (<c>missionigc.cpp:3734</c>). Sector
/// contents are attributed by <c>ClusterId</c> membership; a warp's
/// destination is the sector containing its partner warp (or the sector named
/// directly), matching <c>warpIGC.h:129-141</c> and the corpus validator.
/// </remarks>
public sealed partial class Mission : IMissionIgc
{
    /// <summary>Enabled by the authoritative server; absent in client simulations.</summary>
    public SkillTelemetry? Skills { get; set; }

    /// <summary>P629 stress-test override. Native sector crowding is enabled by default.</summary>
    public bool CrowdingPenaltyEnabled { get; set; } = true;

    /// <summary>P817 experimental native NPC travel, default off. Player and MCP controls are excluded.</summary>
    public bool NativeGotoPlanEnabled { get; set; }

    /// <summary>P872/SIM-0146 — the mission's lifecycle origin. Boot-configured
    /// and admin worlds (every mission the port assembles today) persist
    /// forever, matching the ruling of Domingo 2026-10-28: native lets admin
    /// (<c>bObjectModelCreated</c>) and custom-core games break the empty-mission
    /// reap rule (<c>FedSrv.CPP:7882-7884</c>). A <b>player-created</b> session
    /// (the lobby-directed "create mission before anyone is here" path,
    /// FedSrv.CPP:7867-7869) sets this true and is reaped after ten seconds
    /// empty, exactly like native's <c>CheckForNoShows</c>.</summary>
    public bool PlayerCreated { get; set; }

    /// <summary>P872/SIM-0146 — the server sim-time at which this mission was
    /// created, the port of native's <c>GetStartTime()</c> FILETIME that
    /// <c>CheckForNoShows</c> ages against (<c>FedSrv.CPP:7893-7902</c>). The
    /// host sets it at creation; it stays 0 for missions assembled before the
    /// host knew the clock.</summary>
    public double CreatedAt { get; set; }

    /// <summary>P872/SIM-0750 — the per-mission replay counter
    /// (<c>m_nReplayCount</c>, <c>Igc/missionigc.cpp:3849-3852</c> increments
    /// it on every <c>ResetMission</c> after the first generation and
    /// <c>GetReplayCount</c>, <c>Igc/missionigc.cpp:4211-4214</c>, returns it).
    /// It feeds the context name's trailing <c>%02d</c> and survives rotation:
    /// the host carries the finished mission's count into the rebuilt world
    /// (the port's analogue of native resetting the SAME CmissionIGC object).</summary>
    public short ReplayCount { get; internal set; }

    /// <summary>P872/SIM-0750 — native <c>m_bHasGenerated</c>
    /// (<c>Igc/missionigc.cpp:3850-3857</c>): true once the mission world has
    /// been generated; gates whether the next reset counts as a replay.</summary>
    public bool HasGenerated { get; internal set; }

    /// <summary>P872/SIM-0751 — native <c>m_missionID</c>
    /// (<c>Igc/missionigc.h:613-616</c>), the identifier that appears as the
    /// <c>%04d</c> field of the context name. The host assigns it at assembly.</summary>
    public short MissionId { get; set; }

    /// <summary>P872/SIM-0751 — the mission's creation wall-clock moment, the
    /// port of native's <c>m_ftCreated</c> FILETIME that
    /// <c>GetContextName</c> renders into the <c>U/Z</c> + MMDDHHmmss prefix
    /// (<c>Igc/missionigc.cpp:4225-4230</c>). The host sets it at creation;
    /// it defaults to assembly time.</summary>
    public DateTimeOffset CreatedWallTime { get; internal set; } = DateTimeOffset.UtcNow;

    // P872/SIM-0751 — the cached context name (native m_strContextName,
    // missionigc.cpp:4217-4238: generated once, invalidated on reset).
    private string? _contextName;

    /// <summary>P872/SIM-0750 — the port of the replay bookkeeping inside
    /// native <c>CmissionIGC::ResetMission</c> (<c>Igc/missionigc.cpp:3849-3857</c>):
    /// after the first generation every reset increments the replay count and
    /// invalidates the cached context name so it re-generates with the new
    /// count. The host calls this on each regenerated/rotated world.</summary>
    public void BeginReplay()
    {
        if (HasGenerated)
        {
            ReplayCount++;
            _contextName = null;   // clear, so it re-generates (missionigc.cpp:3854-3855)
        }
        else
        {
            HasGenerated = true;
        }
    }

    /// <summary>P872/SIM-0751 — the port of native
    /// <c>CmissionIGC::GetContextName</c> (<c>Igc/missionigc.cpp:4217-4238</c>):
    /// a cached 17-character string <c>%c%02d%02d%02d%02d%02d%04d%02d</c> —
    /// <c>Z</c> for admin club games else <c>U</c>, the creation wall-time
    /// month/day/hour/minute/second, the mission id modulo 10000, and the
    /// replay count modulo 100. Beyond its Windows event-log origin, native
    /// uses it as the persisted game identity
    /// (<c>FedSrv/fsmission.cpp:2766</c> copies it into the game-results
    /// <c>szGameID</c>), so the port binds it the same way through
    /// <c>LobbyHeartbeat</c>'s game-results report. Only the FILETIME/COM
    /// representation is excluded (the port stores DateTimeOffset).</summary>
    public string GetContextName(bool adminClubGame = false)
    {
        if (_contextName is { } cached) return cached;
        var st = CreatedWallTime;
        string context = string.Create(CultureInfo.InvariantCulture,
            $"{(adminClubGame ? 'Z' : 'U')}{st.Month:00}{st.Day:00}{st.Hour:00}{st.Minute:00}{st.Second:00}{MissionId % 10000:0000}{ReplayCount % 100:00}");
        _contextName = context;
        return context;
    }

    /// <summary>The type universe the mission draws on.</summary>
    public StaticCore Core { get; }

    /// <summary>Native local training owns completion, outside FedSrv match rules
    /// (training/trainingmission.cpp:744-779).</summary>
    public bool IsTraining { get; set; }

    /// <summary>The raw map this mission is assembled from.</summary>
    public MissionMap Map { get; }

    /// <summary>Sectors, one per <c>OT_cluster</c>, in map order.</summary>
    public IReadOnlyList<Sector> Sectors => GetClusters();

    /// <summary>The caller-supplied sides, in the order given.</summary>
    public IReadOnlyList<Side> Sides => GetSides();

    /// <summary>Every warp across every sector, in map order.</summary>
    public IReadOnlyList<ResolvedWarp> Warps => GetWarps();

    /// <summary>
    /// The subset of <see cref="Warps"/> whose destination could not be
    /// resolved (a dangling edge). Reported, never thrown.
    /// </summary>
    public IReadOnlyList<ResolvedWarp> DanglingWarps { get; }

    /// <summary>
    /// The host seam this mission is bound to. A mission cannot tick without
    /// one (<c>missionigc.cpp:3058</c>; cf. the <c>nullptr</c> guard at
    /// <c>:3077</c>), so it is mandatory at assembly and immutable thereafter.
    /// </summary>
    public IIgcSite Site { get; }

    /// <summary>
    /// P465c — the per-hull weapon-muzzle offset resolver/cache shared by every
    /// sector of this mission. Attached by the host (like <see cref="Sector.LandingBays"/>);
    /// when null, <see cref="Sim.Weapon.FireWeapon"/> falls back to a zero muzzle
    /// offset (rounds spawn at the ship centre), preserving the historic behaviour
    /// for missions that never wire a <c>.cvh</c> corpus.
    /// </summary>
    public WeaponMuzzleCache? WeaponMuzzles { get; set; }

    /// <summary>
    /// The mission's last-updated time. Seeded by Assemble to initialTime,
    /// matching CmissionIGC::Initialize(now) (missionigc.cpp:3055-3059), then
    /// advanced by Update even while NotStarted (missionigc.cpp:3180-3181).
    /// Read-only to callers.
    /// </summary>
    public double LastUpdate { get; private set; }

    internal DamageTrackClock DamageClock { get; } = new();

    /// <summary>
    /// The mission stage, controlling the tick's behaviour: only
    /// <see cref="MissionStage.Started"/> / <see cref="MissionStage.Starting"/>
    /// run the real (chunked) pass; any other stage makes
    /// <see cref="Update"/> just advance <see cref="LastUpdate"/> to <c>now</c>.
    /// Initial value <see cref="MissionStage.NotStarted"/>, matching the C++
    /// constructor (<c>missionigc.h:397</c>).
    /// </summary>
    public MissionStage Stage { get; set; } = MissionStage.NotStarted;

    /// <summary>Native bAllowFriendlyFire (shipIGC.cpp:1222-1228). Defaults off.</summary>
    public bool AllowFriendlyFire { get; set; }

    /// <summary>FedSrv/fsmission.cpp:2520: periodic income requires developments.</summary>
    public bool AllowDevelopments { get; set; } = true;

    /// <summary>FedSrv/FedSrv.CPP:13387-13417: permit allied ripcord anchors.</summary>
    public bool AllowAlliedRip { get; set; }

    /// <summary>
    /// P767 / SIM-0100 — the gate for the NPC wingman "getting ammo" resupply
    /// latch. Native's trigger lives in <c>BaseClient::Reload</c>'s
    /// <c>!m_fm.IsConnected()</c> branch (clintlib.cpp:3697-3717) — the offline
    /// client that runs the sim itself — while FedSrv inherits
    /// <c>IgcSite::Reload</c>'s <c>false</c> default (Igc/igc.h:4923), so native
    /// MULTIPLAYER wingmen never do this. This port runs the sim on the server
    /// for both modes, so the port gate stands in for the "offline" condition:
    /// default <c>false</c> (native multiplayer semantics), set <c>true</c> by
    /// <c>SkirmishOptions.CreateConfig</c> for single-player/skirmish games
    /// (<c>GameConfig.NpcWingmanAmmoResupply</c>, the per-mission option).
    /// </summary>
    public bool NpcWingmanAmmoResupply { get; set; }

    /// <summary>Native bAllowAlliedViz (probeigc.cpp:459), firing reveal only.</summary>
    public bool AllowAlliedViz { get; set; }

    /// <summary>
    /// P797 — the port of native <c>MissionParams::bShowMap</c>
    /// (<c>Igc/igc.h:1123</c>, <b>default <c>false</c></b> at <c>igc.h:1244</c>:
    /// "Show all warps at the start of the game"). When set, every aleph is
    /// revealed to every side at mission creation
    /// (<c>Igc/mapmakerigc.cpp:1002-1017</c> <c>RevealMap</c>: “Show every side
    /// every warp”, called from <c>:473-477</c>; the start-time repeat is
    /// <c>Igc/missionigc.cpp:4056-4071</c>). With it off — native's default — a
    /// side knows only the alephs its scanners have seen, which is the whole
    /// point of the P797 fog of war.
    /// </summary>
    public bool ShowMap { get; set; }

    /// <summary>
    /// P797 — the port of native <c>MissionParams::bShowHomeSector</c>
    /// (<c>Igc/igc.h:1148</c>, <b>default <c>false</c></b> at <c>igc.h:1313</c>:
    /// "Show everything in a player's home sector at start of game"). When set,
    /// every model in a cluster where the side owns a station is revealed to
    /// that side (<c>Igc/mapmakerigc.cpp:969-997</c> <c>RevealHomeClusters</c>:
    /// “Make every model in the station's cluster visible to the side”,
    /// called from <c>:470-471</c>). Off by default, so a side starts knowing
    /// only its own stations (the own-side rule, <c>FedSrv.CPP:10907-10912</c>)
    /// plus whatever its scanners actually cover.
    /// </summary>
    public bool ShowHomeSector { get; set; }

    /// <summary>
    /// P820c — the port of native <c>MissionParams::bSquadGame</c>
    /// (<c>Igc/igc.h:1142</c>, <b>default <c>false</c></b> at <c>igc.h:1258</c>):
    /// whether this is a squad game. Only a squad game gives a side a squad,
    /// boots non-members on squad change and relays the squad to clients —
    /// exactly native's gate (<c>AddPlayerToSide</c>'s team-leader squad rule,
    /// fsmission.cpp:661-690; <c>SetSideSquad</c>'s boot, fsmission.cpp:1623-1641;
    /// <c>SET_TEAM_INFO</c>, fsmission.cpp:1584-1590). On a non-squad game the
    /// side keeps <see cref="Side.SquadId"/>/<see cref="Side.SquadName"/>
    /// null and no player is ever booted for a squad reason — the pre-P820b
    /// behaviour. Set from <c>GameConfig.SquadGame</c> at assemble.
    /// </summary>
    public bool SquadGame { get; set; }

    /// <summary>
    /// P854 — the per-side invitation lists of a private game, the port of native
    /// <c>CFSMission::AddInvitation</c> (<c>FedSrv/fsmission.cpp:1168-1174</c>) +
    /// each <c>CFSSide</c>'s <c>m_Invitations</c> vector (<c>FedSrv/fsside.cpp:69-86</c>).
    /// Key = side id; value = that side's list of invited <b>subjects</b>. Mirroring
    /// native, whether a mission requires an invitation (<see cref="RequiresInvitation"/>)
    /// and the lists themselves are set independently: native's SQL list arrives
    /// <i>asynchronously</i> after mission construction (<c>GetInvitations</c> /
    /// <c>GotInvitationList</c>, <c>FedSrv/FedSrv.CPP:1307-1318,785-801</c>), so a
    /// mission can be <see cref="RequiresInvitation"/> with <b>empty</b> lists — the
    /// native "list not yet loaded" window, during which <i>nobody</i> is invited and
    /// every join is refused (<c>IsInvited</c> sees only the loaded rows). The port's
    /// source is the JSON config (already in memory), so the window is normally
    /// instantaneous, but the gates must tolerate the empty pre-load state exactly as
    /// native does (refuse with <c>DPR_PrivateGame</c>).
    /// </summary>
    private readonly Dictionary<short, List<string>> _sideInvitations = new();

    /// <summary>
    /// P854 — whether this mission requires an invitation, the port of native
    /// <c>CFSMission::RequiresInvitation() == (m_nInvitationListID != 0)</c>
    /// (<c>FedSrv/fsmission.h:407-411</c>, set at construction from
    /// <c>misparms.nInvitationListID</c>, fsmission.cpp:217). Set by the server at
    /// assemble when the game config configures any side invitation; independent of
    /// whether the lists have actually been loaded yet (see <see cref="_sideInvitations"/>).
    /// </summary>
    public bool RequiresInvitation { get; set; }

    /// <summary>
    /// P854 — add one invited subject to a side's list. Port of native
    /// <c>CFSMission::AddInvitation</c> → <c>CFSSide::AddInvitation</c>
    /// (fsmission.cpp:1168-1174, fsside.cpp:69-86); native drops an out-of-range side
    /// (<c>sid &gt;= c_cSidesMax</c>) with no side effect, mirrored here. The subject is a
    /// callsign on a non-squad game and a squad name on a squad game
    /// (<see cref="IsSideInvited"/> for the matching rule).
    /// </summary>
    public void AddInvitation(short sideId, string subject)
    {
        if (sideId < 0 || sideId >= Sides.Count) return;
        if (!_sideInvitations.TryGetValue(sideId, out var list))
            _sideInvitations[sideId] = list = new List<string>();
        // Native CFSSide::AddInvitation (fsside.cpp:73-80): a squad game keeps ONE
        // invited squad per side — the list is cleared before the push, and native
        // renames the side to the invited squad (SetSideName(szSubjectName),
        // fsside.cpp:78). P853 / SIM-0491 close-out: the port now performs that
        // rename at the model level (Side.SetName); the server relays the
        // resulting SET_TEAM_INFO after loading the lists (Server.Lifecycle.cs,
        // mirroring SetSideName's whole-mission broadcast, fsmission.cpp:1580-1590).
        // A non-squad game keeps the callsign semantics and never renames
        // (fsside.cpp:72 IsSquadGame() gate).
        if (SquadGame)
        {
            list.Clear();
            Sides[sideId].SetName(subject);
        }
        list.Add(subject);
    }

    /// <summary>
    /// P854 — bulk-load the per-side invitation lists at mission assembly (the port's
    /// synchronous-config substitute for native's async SQL round trip,
    /// <c>FedSrv.CPP:1307-1318</c>). Idempotent: adds to whatever is already loaded, so
    /// an async source could later call it again and the gates remain correct throughout.
    /// </summary>
    public void LoadInvitations(IReadOnlyDictionary<short, IReadOnlyList<string>> bySide)
    {
        foreach (var (sideId, subjects) in bySide)
            foreach (var s in subjects)
                AddInvitation(sideId, s);
    }

    /// <summary>
    /// P854 — the current per-side invitation subjects (the loaded subset — empty for a
    /// <see cref="RequiresInvitation"/> mission whose lists have not yet arrived).
    /// Exposed read-only for the join gates and tests.
    /// </summary>
    public IReadOnlyDictionary<short, IReadOnlyList<string>> SideInvitations
        => _sideInvitations.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.AsReadOnly());

    /// <summary>
    /// P854 — native <c>CFSSide::IsInvited</c> (<c>FedSrv/fsside.cpp:34-64</c>), pure
    /// form: whether <paramref name="playerName"/> (with the given active squad
    /// <paramref name="squadNames"/>) is on <paramref name="sideId"/>'s invitation list.
    /// Matching copies native exactly — a <b>squad game</b> matches each entry against
    /// the player's squad memberships, a <b>non-squad</b> game matches each entry
    /// against the player's callsign, both case-insensitive (<c>_stricmp</c>). An
    /// empty/absent side list (including the not-yet-loaded window) invites nobody.
    /// </summary>
    public bool IsSideInvited(short sideId, string playerName, IReadOnlyCollection<string> squadNames)
    {
        if (!_sideInvitations.TryGetValue(sideId, out var list)) return false;
        if (SquadGame)
            return squadNames.Any(sq => list.Any(inv => string.Equals(inv, sq, StringComparison.OrdinalIgnoreCase)));
        return list.Any(inv => string.Equals(inv, playerName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// P854 — native <c>CFSMission::IsInvited</c> (<c>FedSrv/fsmission.cpp:1182-1192</c>,
    /// "any side invites this player"), pure form. Native asserts
    /// <c>RequiresInvitation()</c> at its call sites (<c>FedSrv.CPP:584</c>); callers
    /// gate on <see cref="RequiresInvitation"/> first, as native does.
    /// </summary>
    public bool IsInvited(string playerName, IReadOnlyCollection<string> squadNames)
    {
        foreach (var side in Sides)
            if (IsSideInvited(side.Id, playerName, squadNames)) return true;
        return false;
    }

    private bool _startingVisibilitySeeded;
    private bool _startingVisibilitySwept;

    /// <summary>
    /// P797 — seed every side's visibility from the mission's starting content,
    /// the port of the two things native guarantees before the first client can
    /// look at the map:
    /// <list type="number">
    /// <item><b>The start-of-game reveals</b> — <c>bShowHomeSector</c> /
    /// <c>bAllowAlliedViz</c> / <c>bShowMap</c> when configured
    /// (<c>Igc/mapmakerigc.cpp:470-477</c>, in native order; all no-ops at their
    /// native-default false).</item>
    /// <item><b>One full scanner sweep</b> — the port's own equivalent of the
    /// first <c>Cluster::Update</c> pass running every model's
    /// <c>UpdateSeenBySide</c> (<c>Igc/clusterIGC.cpp:347-357</c>): the own-side
    /// rule makes a side's own stations and ships visible
    /// (<c>FedSrv.CPP:10907-10912</c>) and each station's scanner range covers
    /// its home sector (<c>stationIGC.h:308-328</c>), which is exactly "what its
    /// starting ships/stations can see at start". Running it here means the
    /// join export (<c>fsmission.cpp:4113-4250</c>) and the discovery sweep read
    /// real flags even for a peer that joins before the first live sector tick.
    /// </item>
    /// </list>
    /// Idempotent: the flags only ever grow (statics latch, ships are re-derived
    /// every live sweep), so re-running it is harmless and later live sweeps take
    /// over anyway. Called by the server at the Started transition and before a
    /// join export.
    /// </summary>
    public void SeedStartingVisibility()
    {
        if (!_startingVisibilitySeeded)
        {
            _startingVisibilitySeeded = true;
            // Reveals first, exactly native's mapmaker order (RevealHomeClusters /
            // RevealMap run before ActivateSides, mapmakerigc.cpp:470-479), so the
            // sweep below cannot immediately un-see a revealed object.
            if (ShowHomeSector) RevealHomeClusters();
            // P856 — native's order (mapmakerigc.cpp:470-477): home, then the
            // allied reveal, then the full map reveal.
            if (AllowAlliedViz) RevealAlliedClusters();
            if (ShowMap) RevealMap();
        }
        // The sweep itself is one-shot: it is the start-of-game pass. Later live
        // sweeps (every Sector.c_nPassesPerUpdate) keep the flags current, and a
        // peer joining mid-game reads exactly those live flags.
        if (!_startingVisibilitySwept)
        {
            _startingVisibilitySwept = true;
            foreach (var sector in Sectors)
                sector.ResolveCrossSideVisibility();
        }
    }

    /// <summary>P797 — native <c>CmapMakerIGC::RevealHomeClusters</c>
    /// (<c>Igc/mapmakerigc.cpp:969-997</c>): for every side, make every model in
    /// a cluster where that side owns a station visible to it.</summary>
    private void RevealHomeClusters()
    {
        foreach (var side in Sides)
            foreach (var sector in Sectors)
            {
                bool owned = false;
                foreach (var station in sector.StationRuntimes)
                    if (station.SideId == side.Id && !station.Dead) { owned = true; break; }
                if (!owned) continue;
                foreach (var model in sector.Models)
                    model.SideVisibility.SetSideVisibility(side.Id, true);
                foreach (var runtimestation in sector.StationRuntimes)
                    runtimestation.SideVisibility.SetSideVisibility(side.Id, true);
                foreach (var rock in sector.Mineable)
                    rock.SideVisibility.SetSideVisibility(side.Id, true);
                foreach (var warp in sector.Warps)
                    warp.SideVisibility.SetSideVisibility(side.Id, true);
            }
    }

    /// <summary>P856 — native <c>CmapMakerIGC::RevealAlliedClusters</c>
    /// (<c>Igc/mapmakerigc.cpp:1020-1058</c>, called at <c>:473-474</c> only when
    /// <c>MissionParams::bAllowAlliedViz</c> is set): for every side, make every
    /// model in a cluster where that side owns a station visible to its
    /// <b>allies</b> — the native loop reveals each cluster model to every side
    /// whose <c>AlliedSides</c> test passes, explicitly skipping the owning side
    /// (already covered by <see cref="RevealHomeClusters"/>) and every
    /// <c>OT_warp</c> ("alephs must be replicated").</summary>
    private void RevealAlliedClusters()
    {
        foreach (var side in Sides)
            foreach (var sector in Sectors)
            {
                bool owned = false;
                foreach (var station in sector.StationRuntimes)
                    if (station.SideId == side.Id && !station.Dead) { owned = true; break; }
                if (!owned) continue;
                foreach (var ally in Sides)
                {
                    if (ally.Id == side.Id || !AreSidesFriendly(side.Id, ally.Id)) continue;
                    foreach (var model in sector.Models)
                        model.SideVisibility.SetSideVisibility(ally.Id, true);
                    foreach (var runtimestation in sector.StationRuntimes)
                        runtimestation.SideVisibility.SetSideVisibility(ally.Id, true);
                    foreach (var rock in sector.Mineable)
                        rock.SideVisibility.SetSideVisibility(ally.Id, true);
                    // Warps are deliberately excluded (mapmakerigc.cpp:1050).
                }
            }
    }

    /// <summary>P797 — native <c>CmapMakerIGC::RevealMap</c>
    /// (<c>Igc/mapmakerigc.cpp:999-1018</c>, repeated at mission start by
    /// <c>Igc/missionigc.cpp:4056-4071</c>): every aleph visible to every side.</summary>
    private void RevealMap()
    {
        foreach (var sector in Sectors)
            foreach (var warp in sector.Warps)
                foreach (var side in Sides)
                    warp.SideVisibility.SetSideVisibility(side.Id, true);
    }

    /// <summary>Explicit allied side pairs, in addition to the live alliance
    /// groups on <see cref="Side.AllyGroup"/>. Used by standalone hosts/tests
    /// that never run the lobby's <c>CHANGE_ALLIANCE(196)</c> handler; either
    /// ordering represents the same alliance.</summary>
    public HashSet<(short, short)> AlliedSidePairs { get; } = [];

    /// <summary>P856 — native <c>IsideIGC::AlliedSides</c>
    /// (<c>Igc/igc.h:4297-4305</c>): same side counts as friendly, otherwise both
    /// sides must carry the same non-<c>NA</c> alliance group. Reads the live
    /// groups the server's <c>UpdateAlliances</c> writes
    /// (<c>FedSrv/fsmission.cpp:5638-5811</c>, port P190), so the allied legs of
    /// friendly fire (<c>Igc/shipIGC.cpp:1223-1224</c>), probe immunity
    /// (<c>Igc/probeigc.h:75</c>) and allied visualization
    /// (<c>FedSrv/FedSrv.CPP:10893-10895</c>) are reachable in production. The
    /// explicit <see cref="AlliedSidePairs"/> set is the host/test override.</summary>
    internal bool AreSidesFriendly(short? a, short? b)
    {
        if (a is not { } x || b is not { } y) return false;
        if (x == y) return true;
        if (AlliedSidePairs.Contains((x, y)) || AlliedSidePairs.Contains((y, x))) return true;
        sbyte? groupX = null, groupY = null;
        bool foundX = false, foundY = false;
        foreach (var side in Sides)
        {
            if (side.Id == x) { groupX = side.AllyGroup; foundX = true; }
            if (side.Id == y) { groupY = side.AllyGroup; foundY = true; }
        }
        return foundX && foundY && groupX is not null && groupX == groupY;
    }

    /// <summary>
    /// The <see cref="Side.Id"/> of the side that currently owns the mission —
    /// the port of native <c>m_misdef.iSideMissionOwner</c>
    /// (<c>fsmission.cpp:584-592</c>). <c>null</c> means no side owns it yet.
    /// Per-<em>mission</em>, not per-side (P183), so it lives here alongside
    /// <see cref="Stage"/> rather than on <see cref="Side"/>. Computed only at
    /// the two real event sites <c>AddPlayerToSide</c> (a new owner arises iff
    /// no side owns the mission, or the owning side has no players — always
    /// alongside a new team leader, <c>assert(IMPLIES(fMissionOwner,
    /// fTeamLeader))</c>) and <c>RemovePlayerFromSide</c> (when the owning
    /// side empties, the owner role migrates to another occupied side's
    /// existing leader, or reverts to <c>null</c> if none). Starts
    /// <c>null</c>. Written by the Server's side-switch capability and its
    /// disconnect leader-succession.
    /// </summary>
    public short? MissionOwnerSideId { get; set; }

    /// <summary>
    /// Whether the mission lobby is locked (P186) — the port of the native
    /// per-mission <c>m_misdef.bLockLobby</c> flag (<c>fsmission.cpp</c>), set by
    /// <c>LOCK_LOBBY(43)</c> and consulted only by the mission owner. Native's
    /// <em>real</em> downstream effect of this flag — refusing a brand-new
    /// player joining the mission from the lobby for the first time
    /// (<c>FedSrv.CPP:733</c>, <c>DPR_LobbyLocked</c>) — is part of the legacy
    /// lobby/rejoin machinery (<c>OldPlayerLink</c>/<c>bAllowJoiners</c>/
    /// <c>nTeams</c>) this port has already ruled N/A (LOBBY_SOCIAL_NA_FINDINGS
    /// _P129): this port seats players directly via config, not through a
    /// native "first join" flow, so there is no hook point to attach the lock
    /// to. The flag therefore exists for wire completeness and any future
    /// client UI — it is broadcast and stored, but not enforced anywhere yet.
    /// This honesty note mirrors the port's established convention (cf.
    /// <see cref="LockSides"/>, which DOES have a real reachable hook).
    /// </summary>
    public bool LockLobby { get; set; }

    /// <summary>
    /// Whether team (side) changes are locked (P186) — the port of the native
    /// per-mission <c>m_misdef.bLockSides</c> flag (<c>fsmission.cpp</c>), set
    /// by <c>LOCK_SIDES(44)</c>. Unlike <see cref="LockLobby"/>, this one has a
    /// real, reachable hook in this port: the P184 <c>POSITIONREQ</c> /
    /// <c>TryJoinSide</c> side-switch flow refuses a side-change request with
    /// <c>DELPOSITIONREQ(DPR_SidesLocked)</c> when this flag is true, mirroring
    /// native <c>CheckPositionRequest</c>'s <c>bLockSides</c> gate
    /// (<c>fsmission.cpp:4590</c>).
    /// </summary>
    public bool LockSides { get; set; }

    /// <summary>
    /// Whether the mission allows <b>empty teams</b> (P202) — the port of the
    /// native per-mission <c>m_misdef.misparms.bAllowEmptyTeams</c>
    /// (<c>fsmission.cpp:4440</c> consults it with the per-side
    /// <c>rgfActive</c> flags), toggled automatically by the
    /// <c>SIDE_INACTIVE(68)</c> handler (<c>FedSrv.CPP:7395-7414</c>): AET is
    /// ON iff at least one side is currently inactive
    /// (<see cref="Side.Active"/> false). Starts <c>false</c> (every side
    /// active). The flag is computed from — and kept in sync with — the
    /// sides' active states; there is no independent way to set it. It is
    /// reported to the whole mission inside the server's
    /// <c>SIDE_INACTIVE</c> reply via <c>bChangeAET</c>/<c>bAET</c>. Its real
    /// native consumption (permitting a side with no players to exist /
    /// skipping such sides in the ready-up tally, <c>fsmission.cpp:4397,4440</c>)
    /// is part of the roster machinery this port plays out purely through the
    /// toggle; the field exists to carry the wire state faithfully.
    /// </summary>
    public bool AllowEmptyTeams { get; set; }

    /// <summary>Authoritative native goal parameters; selection and thresholds
    /// share one source (Igc/igc.h:1511-1554).</summary>
    public MissionGoals Goals { get; set; } = new();
    public bool ConquestEnabled => Goals.IsConquestGame;
    public bool TerritoryEnabled => Goals.IsTerritoryGame;
    public bool ProsperityEnabled => Goals.IsProsperityGame;
    public bool ArtifactEnabled => Goals.IsArtifactsGame;
    public bool FlagsEnabled => Goals.IsFlagsGame;
    public bool KillsEnabled => Goals.IsDeathMatchGame;
    public short ArtifactWinGoal => Goals.Artifacts;
    public short FlagsWinGoal => Goals.Flags;
    public short KillsWinGoal => Goals.TeamKills;

    /// <summary>
    /// HUD-12 — the native <c>MissionParams::iLives</c> per-player life budget
    /// (<c>src/Igc/igc.h:1185</c>, "Player must die more than this number of
    /// times to exit"; <c>c_cUnlimitedLives = 0x7fff</c>, <c>igc.h:1007,1291</c>).
    /// The native HUD shows <c>iLives − ppi-&gt;MissionDeaths()</c> and only draws
    /// the lives badge while that is below 10 (<c>WinTrek/console.cpp:812-817</c>).
    /// The port does not enforce elimination (this is a presentation parameter,
    /// sent to the client in <c>MISSIONDEF(38)</c>); the default is native's
    /// unlimited sentinel, so ordinary port missions never show the badge.
    /// </summary>
    public short Lives { get; set; } = Allegiance.Core.Wire.MissionDef.UnlimitedLives;

    /// <summary>
    /// P302 — the native <c>c_didTeamMoney</c> development id (<c>src/Igc/igc.h:512</c>):
    /// the special economic-victory development whose completion ends a
    /// <see cref="ProsperityEnabled"/> game (<c>FedSrv.CPP:11274-11279</c>;
    /// <c>DevelopmentLogic.IsTechOnly</c> treats it as never tech-only,
    /// <c>developmentigc.cpp:32-47</c>).</summary>
    public const short TeamMoneyDevelopmentId = 1;   // c_didTeamMoney (igc.h:512)

    /// <summary>Configured native conquest threshold (FedSrv/fsmission.cpp:3624).</summary>
    public short ConquestWinThreshold => Goals.ConquestPercentage;
    /// <summary>Configured native territory threshold (FedSrv/fsmission.cpp:3663).</summary>
    public short TerritoryWinThresholdPercent => Goals.TerritoryPercentage;

    /// <summary>
    /// The chunk-size upper bound, faithful to <c>c_fTimeIncrement = 0.25f</c>
    /// (<c>src/Igc/igc.h:5120</c>). Each mission tick splits the elapsed time
    /// into chunks of <em>at most</em> this duration (see <see cref="Update"/>).
    /// </summary>
    public const double TimeIncrement = 0.25;

    /// <summary>Loaded c_fcidSecondsBetweenPaydays (slot 6), read by
    /// fsmission.cpp:2253,2516. Core import overrides constructor defaults
    /// (missionigc.cpp:3283; missionigc.h:171-173).</summary>
    public double SecondsBetweenPaydays => Core.Constants.FloatConstants[6];

    /// <summary>Loaded c_fcidIncome (slot 30), fsmission.cpp:2523.</summary>
    public float IncomePerPayday => Core.Constants.FloatConstants[30];

    /// <summary>Loaded c_fcidStartingMoney (slot 11), fsmission.cpp:1992.</summary>
    public float StartingMoneyPerSide => Core.Constants.FloatConstants[11];

    /// <summary>P443 — the <c>MissionParams.fStartingMoney</c> multiplier in the
    /// seed formula <c>StartingMoneyPerSide × (fStartingMoney + civ.BonusMoney)</c>
    /// (<c>fsmission.cpp:1992-2001</c>). <b>Honesty note:</b> the native mission
    /// params header that holds <c>fStartingMoney</c>'s default is <b>not</b> in
    /// this checkout — only <c>AGCGameParameters.cpp:1058,1074</c> and WinTrek's
    /// "StartingMoneyValues" combo (<c>newgamescreen.cpp:194,1009</c>) reference
    /// it. Following this repo's honesty-about-unrecoverable-native-values
    /// convention (cf. <see cref="VisibilityRangeDefault"/>), we adopt a
    /// multiplier of <b>1.0</b> as a named stated default — the value that makes
    /// a zero-bonus civ seed exactly <c>StartingMoneyPerSide</c>. No config is
    /// added. The <c>≤ 0</c> guard in the seed (<c>fsmission.cpp:2001-2002</c>,
    /// where a non-positive multiplier yields 0) is preserved for faithfulness.</summary>
    public const float StartingMoneyMultiplier = 1.0f;

    /// <summary>
    /// P259 — the baseline per-side <b>cluster visibility range</b>, seeded onto
    /// every side at <see cref="Assemble"/> (see <see cref="Side.VisibilityRange"/>)
    /// and used by <see cref="CanSideSeeCluster"/>/<see cref="Side.CanSeeCluster"/>
    /// to decide which clusters a side has on its radar.
    /// <para><b>Honesty note (stated default — see the P259 writeup).</b> This is
    /// the port of the original 2003 Microsoft Allegiance per-side visibility-range
    /// constant <c>c_fcidVisibilityRange</c> that the on-disk native reference
    /// (<c>~/Development/Allegiance</c>, modern FreeAllegiance) <b>removed</b>:
    /// its <c>CFSSide</c>/<c>UpdateVisibility</c>/<c>CanSeeCluster</c>/<c>m_visibilityRange</c>
    /// machinery does not exist there (sideigc.cpp has no such functions), and the
    /// original source bearing the real default could not be retrieved. The value is
    /// therefore a <b>stated default</b>, following this repo's established
    /// honesty-about-unrecoverable-native-values convention. This strategic
    /// visibility fallback is separate from the recovered goal parameters.
    /// Because the cross-cluster distance
    /// proxy measures the strategic-map screen space (normalized coordinates,
    /// <see cref="DataClusterIGC.ScreenX"/>/<see cref="DataClusterIGC.ScreenY"/>,
    /// ~0..1), the default is expressed in that space: 0.5 reveals an owned
    /// asset's neighbours within half a map-screen unit, not the whole map
    /// (a probe — P260 — later widens it).</para>
    /// </summary>
    public const float VisibilityRangeDefault = 0.5f;

    /// <summary>
    /// P259 — how often the per-side visible-cluster set is recomputed: every
    /// <b>200</b> mission <see cref="Update"/>s, not every tick — mirroring this
    /// port's existing periodic-not-every-tick precedent
    /// <c>Server.ClusterInfoRefreshTicks = 200</c> (Server.cs:1070; 10 s at 20 upd/s).
    /// The original native interval (<c>CmissionIGC::UpdateVisibility</c>) is
    /// unverifiable from the on-disk FreeAllegiance tree (the feature was removed
    /// there), so this adopts the port's own periodic cadence as the stated
    /// interval — see the P259 writeup.
    /// </summary>
    public const int VisibilityRefreshTicks = 200;

    // Object-id space for models created by the mission at runtime (projectiles
    // today; ships/stations etc. later) — the analogue of the C++ CreateObject
    // id assignment. Starts well clear of the small ids the flight/bot tests
    // use for ships, so a projectile can never alias a ship in the model list.
    private int _nextObjectId = 71000;

    // P259 — counts mission Updates since the last periodic side-visibility
    // refresh, so the recompute fires every VisibilityRefreshTicks Updates
    // (a per-tick side pass would be wasteful; visibility changes slowly).
    private int _visibilityRefreshCounter;

    /// <summary>
    /// The sim time of the <em>next</em> payday fire — the port of the FedSrv
    /// <c>m_timeNextPayday</c> (<c>fsmission.cpp:2253</c>: seeded to
    /// <c>mission-start time + 60</c>; <c>fsmission.cpp:2519</c>: re-armed by
    /// adding the interval, keeping phase). 0 means "not armed yet" — it is
    /// seeded lazily on the first <c>Started</c>/<c>Starting</c> tick, because
    /// this port has no distinct "mission start" moment (Stage flips to
    /// Started externally, after <see cref="Assemble"/>); tying the first
    /// payday to the first Started tick is the faithful stand-in for
    /// mission-start. Subsequent fires preserve the original phase
    /// (<c>+= SecondsBetweenPaydays</c>), so an irregular tick cadence cannot
    /// drift the schedule. Accessible to tests for introspection.
    /// </summary>
    internal double NextPaydayTime { get; set; }

    /// <summary>Allocate the next runtime object id. P316: the id space is a
    /// finite 16-bit <c>short</c> (native <c>typedef short ObjectID</c>,
    /// <c>Igc/igc.h:477</c> — the wire field width is 16 bits, so it cannot be
    /// widened without breaking compatibility), and the C++ counter wraps
    /// silently at the boundary. Wrapping blindly lets a freshly created
    /// projectile/missile/mine/probe/treasure/chaff shadow a <em>still-live</em>
    /// model's id, mis-routing target lookups. The fix (option (b)) is to walk
    /// forward past any id currently held by a live model in this mission at
    /// allocation time. Owned by the mission so ids are unique across every
    /// model it creates, not just per weapon. When nothing collides the
    /// sequence is identical to the old unchecked counter.</summary>
    /// <remarks>Public (rather than <c>internal</c>) so the owning Server — a
    /// separate assembly — can allocate an id for a treasure it places directly
    /// (the P411 flag respawned at a remote pedestal station, Server.Combat.cs).</remarks>
    public short NextObjectId()
    {
        for (int attempt = 0; attempt < 65536; attempt++)
        {
            short candidate = unchecked((short)_nextObjectId++);
            if (!IsObjectIdLive(candidate))
                return candidate;
        }
        // Pathological: the entire 16-bit space is occupied. Hand out the last
        // visited value rather than spinning forever.
        return unchecked((short)(_nextObjectId - 1));
    }

    /// <summary>Whether <paramref name="id"/> is currently held by any live
    /// model in this mission's sectors — the allocation-time collision check
    /// backing <see cref="NextObjectId"/> (P316). Enumerating the sector model
    /// lists is O(live models) per allocation, negligible at creation-time
    /// frequency (not per-tick).</summary>
    private bool IsObjectIdLive(short id)
    {
        foreach (var sector in Sectors)
            foreach (var model in sector.Models)
                if (model.Id == id)
                    return true;
        return false;
    }

    private Mission(
        StaticCore core,
        MissionMap map,
        IReadOnlyList<Sector> sectors,
        IReadOnlyList<Side> sides,
        IReadOnlyList<ResolvedWarp> allWarps,
        IReadOnlyList<ResolvedWarp> danglingWarps,
        IIgcSite site)
    {
        Core = core;
        _coreLease = StaticCoreCache.Shared.Retain(core);
        Site = site;
        Map = map;
        foreach (var sector in sectors) AddCluster(sector);
        foreach (var side in sides) AddSide(side);
        foreach (var warp in allWarps) AddWarp(warp);
        DanglingWarps = danglingWarps;
        Site = site;
    }

    /// <summary>Selected plans keyed by side id; consumed by the P703 commander.</summary>
    public Dictionary<short, ResearchPlan> ResearchPlans { get; } = new();

    /// <summary>P703 port-extension identity for client executor reset across rotations.</summary>
    public string ResearchEpoch { get; } = Guid.NewGuid().ToString("N");
    /// <summary>P703 explicit showcase extension; scales periodic native income only, before
    /// DoPayday distributes it to player wallets. Starting money and mining are unchanged.</summary>
    /// <summary>Port-only showcase research accelerator. Native bucket and construction timers use 1.</summary>
    public double ShowcaseResearchTimeScale { get; set; } = 1;
    public double ShowcaseIncomeMultiplier { get; set; } = 1;

    public ResearchSettings Research { get; private set; } = new();

    /// <summary>
    /// Assemble the mission object graph from a core, a map and the side
    /// specifications. Throws on a side naming a civilization absent from the
    /// core, or on a member (warp/station/asteroid) naming a sector id that has
    /// no <c>OT_cluster</c> record in the map. Warp <i>destinations</i> that
    /// cannot resolve are reported in <see cref="DanglingWarps"/>, not thrown —
    /// they never occur in the shipped corpus (measured 0 / 5212).
    /// </summary>
    public static Mission Assemble(
        StaticCore core,
        MissionMap map,
        IReadOnlyList<SideSpec> sides,
        IIgcSite site,
        double initialTime = 0,
        ResearchSettings? research = null,
        MissionGoals? goals = null)
    {
        if (!double.IsFinite(initialTime) || initialTime < 0)
            throw new ArgumentOutOfRangeException(nameof(initialTime));
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(sides);
        ArgumentNullException.ThrowIfNull(site);

        goals = goals is null ? new() : goals with { };
        goals.Validate();
        research ??= new ResearchSettings();
        research.Validate();
        // 1. Resolve the sides' civilizations from the core.
        var sideList = new List<Side>(sides.Count);
        foreach (var spec in sides)
        {
            if (!core.Civilizations.TryGetValue(spec.CivilizationId, out var civ))
                throw new InvalidOperationException(
                    $"side {spec.Id} ('{spec.Name}') names civilization {spec.CivilizationId} " +
                    $"absent from core {core.Version}; a side's civilization must exist in the core");
            sideList.Add(new Side(spec.Id, spec.Name, civ,
                research.AllowDevelopments ? BucketCatalog.ForSide(core, site, research.StartingTechs(civ),
                    // P478 — the side's GA at bucket creation is the civ's base
                    // attribute set (no development has completed yet), exactly
                    // what native reads while CreateBuckets runs.
                    GlobalAttributeSet.FromArray(civ.GasBaseAttributes),
                    goals.IsProsperityGame ? goals.TeamMoney : 1) : Array.Empty<Bucket>()));
            sideList[^1].DevelopmentTechs = research.StartingTechs(civ);
            sideList[^1].InitialTechs = sideList[^1].DevelopmentTechs;
            // P363 — seed each side's colour from the native per-side-id palette
            // (missionigc.cpp:3734-3746), the port of `ds.color.SetRGBA(...)`.
            var (cr, cg, cb) = SidePalette.ColorFor(spec.Id);
            sideList[^1].ColorR = cr;
            sideList[^1].ColorG = cg;
            sideList[^1].ColorB = cb;
        }

        // 2. One sector per OT_cluster, keyed by ClusterId. ClusterIds are unique
        //    across the whole shipped corpus (measured 0 duplicates in 111 maps);
        //    MissionMap tolerates a duplicate, in which case every member naming
        //    that id attaches to every sector sharing it.
        var clusterIds = new HashSet<short>(map.Clusters.Select(c => c.ClusterId));
        short RequireSector(short clusterId, string kind, string name)
        {
            if (clusterIds.Contains(clusterId)) return clusterId;
            throw new InvalidOperationException(
                $"{kind} '{name}' names cluster {clusterId} which has no OT_cluster record " +
                "in this map; a member must belong to a real sector");
        }

        var sectorsById = new Dictionary<short, Sector>(map.Clusters.Count);
        foreach (var cluster in map.Clusters)
            sectorsById.Add(cluster.ClusterId, new Sector(cluster, initialTime));

        // 3. Attribute stations and asteroids by ClusterId.
        foreach (var station in map.Stations)
            RequireSector(station.ClusterId, "station", station.Name);
        foreach (var g in map.Asteroids.GroupBy(a => RequireSector(a.ClusterId, "asteroid", a.Name)))
            sectorsById[g.Key].AttachAsteroids(g);

        // 4. Attribute warps by ClusterId and resolve each warp's destination to
        //    a sector. A warp's DestinationId names its partner warp; the
        //    destination sector is that partner warp's owning cluster. The
        //    validator also accepts a DestinationId that is itself a sector id
        //    (CrossRefValidator.cs:139-143), handled here too. Unresolvable
        //    destinations are collected into DanglingWarps, not thrown.
        var warpOwner = new Dictionary<short, short>(map.Warps.Count);
        foreach (var w in map.Warps)
            warpOwner[w.WarpId] = RequireSector(w.ClusterId, "warp", w.Name);

        var allWarps = new List<ResolvedWarp>(map.Warps.Count);
        var dangling = new List<ResolvedWarp>();
        foreach (var w in map.Warps)
        {
            Sector? destination = null;
            if (warpOwner.TryGetValue(w.DestinationId, out var partnerOwner))
                destination = sectorsById[partnerOwner];
            else if (sectorsById.TryGetValue(w.DestinationId, out var directSector))
                destination = directSector;

            var resolved = new ResolvedWarp(w, destination);
            allWarps.Add(resolved);
            if (destination is null) dangling.Add(resolved);
            sectorsById[w.ClusterId].AttachWarp(resolved);
        }

        var sectors = map.Clusters.Select(c => sectorsById[c.ClusterId]).ToList();
        // Give each sector its mission's side set (the port of the native
        // pcluster->GetMission()->GetSides() lookup, FedSrv.CPP:10887-10888) so
        // the P132 cross-side visibility sweep can reconcile every side — even
        // one with no presence in the sector — to its correct SeenBySide value.
        foreach (var s in sectors) s.MissionSides = sideList;
        // Native CmissionIGC::Initialize(now) seeds m_lastUpdate (missionigc.cpp:3055-3059).
        // Seed BEFORE any ship placement or countdown can observe this world.
        var mission = new Mission(core, map, sectors, sideList, allWarps, dangling, site)
        { LastUpdate = initialTime, Goals = goals, Research = research, AllowDevelopments = research.AllowDevelopments };
        mission.DamageClock.Initialize(initialTime);
        foreach (var sector in sectors)
        {
            sector.Mission = mission;
            sector.MissionSides = mission.Sides;
            foreach (var asteroid in sector.Mineable) { asteroid.Mission = mission; asteroid.Sector = sector; mission.AddAsteroid(asteroid); }
        }
        // P259 — wire the sides back to the mission (the analogue of the Sector
        // back-link just above) and seed each side's cluster visibility range
        // from the mission constant, so Side.CanSeeCluster can query live state.
        foreach (var side in sideList)
        {
            side.Mission = mission;
            side.VisibilityRange = VisibilityRangeDefault;
        }
        // Register runtime stations after their mission/side links exist. Native
        // stationIGC.cpp:65-75 notifies after each addition, so distinct tech
        // gains during initial construction must not collapse into one event.
        foreach (var station in map.Stations)
        {
            var sector = sectorsById[station.ClusterId];
            if (core.StationTypes.TryGetValue(station.StationTypeId, out var type))
                mission.PlaceStation(new Station(station, type), sector);
            else
                sector.AttachStations([station], core); // retain the static-only fallback
        }
        return mission;
    }

    /// <summary>
    /// Advance the mission to <paramref name="now"/>, replicating
    /// <c>CmissionIGC::Update</c> (<c>missionigc.cpp:3127-3185</c>) exactly.
    /// The cadence and ordering are the point of this task; objects come later.
    /// </summary>
    /// <remarks>
    /// Three behaviors, faithful to the C++:
    /// <list type="number">
    /// <item><b>Clock guard</b> — a backwards <c>now</c> throws (the C++
    /// <c>assert (now &gt;= m_lastUpdate)</c>); an unchanged <c>now</c> is a
    /// no-op.</item>
    /// <item><b>Chunking</b> — only <c>Started</c>/<c>Starting</c> missions tick.
    /// Elapsed <c>deltaT = now - LastUpdate</c> splits into
    /// <c>n = 1 + (int)(deltaT / <see cref="TimeIncrement"/>)</c> evenly-divided
    /// chunks (<b>not</b> fixed 250&nbsp;ms steps): each chunk is
    /// <c>startTime + deltaT·(i/n)</c>, so every chunk is <em>at most</em> the
    /// increment and the last lands exactly on <c>now</c>. The C++ guards
    /// <c>now - m_lastUpdate &lt; 600</c> (&quot;never 10 minutes at a time&quot;);
    /// ours throws rather than silently clamping (decision documented in the
    /// README).</item>
    /// <item><b>Cadence</b> — sides update <em>once per Update</em>, clusters
    /// update <em>once per chunk</em>. All side updates precede the first
    /// cluster chunk, per the C++ loop order.</item>
    /// </list>
    /// The postcondition <c>LastUpdate == now</c> holds on every path. Side
    /// updates are witnessed through the <see cref="IIgcSite.SideUpdated"/>
    /// observation hook; each cluster chunk is witnessed through
    /// <see cref="IIgcSite.ClusterUpdateEvent"/> (the C++'s only site call in
    /// the tick, <c>clusterIGC.cpp:357</c>). On payday ticks the side pass is
    /// additionally witnessed through <see cref="IIgcSite.PaydayEvent"/>, the
    /// P51 seam (see <see cref="NextPaydayTime"/>).
    /// </remarks>
    public void Update(double now)
    {
        if (IsDisposed) return;
        if (now < LastUpdate)
            throw new InvalidOperationException(
                $"mission clock moved backwards (now = {now:R} < LastUpdate = {LastUpdate:R}); " +
                "Update requires a monotonically increasing time (missionigc.cpp:3129)");

        if (now == LastUpdate) return;                 // nothing to do; postcondition holds

        if (Stage is not (MissionStage.Started or MissionStage.Starting))
        {
            LastUpdate = now;                          // not started: just advance (c++:3180-3181)
            return;
        }

        // ── once per Update: the side pass ────────────────────────────────
        foreach (var side in Sides)
        {
            side.Update(now);
            Site.SideUpdated(side, now);               // observation hook (instrumentation)
        }

        DamageClock.Update(now); // Igc/missionigc.cpp:3146, once before sector chunks

        // ── P259 — periodic per-side cluster-visibility refresh ──────────
        // Recomputed every VisibilityRefreshTicks Updates (not every tick) —
        // visibility changes slowly, so a per-tick pass would be wasteful. See
        // RefreshSideVisibility for the recompute itself.
        if (++_visibilityRefreshCounter >= VisibilityRefreshTicks)
        {
            _visibilityRefreshCounter = 0;
            RefreshSideVisibility();
        }

        // ── payday (P51/P443): the reference DoTick payday block (fsmission.cpp:
        // 2514-2552), folded into the same per-Update pass. Seed on the first
        // Started tick, then fire when now crosses the armed time, accruing
        // each side's income — the base <c>IncomePerPayday × (1 + IncomeMoney)</c>
        // (fsmission.cpp:2536) plus Σ over the side's stations of
        // <c>GetStationType()->GetIncome()</c> (fsmission.cpp:2538-2546; see
        // <see cref="StationIncomeFor"/>, which reads the live runtime stations
        // so capture re-siding and destruction fall out of current ownership) —
        // and reporting it through the site seam.
        // Distribution to players (delta split + FM_S_PAYDAY send + deduction)
        // lives in the host's PaydayEvent handler, mirroring the split between
        // fsmission.cpp:2514-2552 (accrual) and fsmission.cpp:2294-2326 (payout).
        // Timeout wins before DoTick's economy (FedSrv/fsmission.cpp:2508-2516).
        // The host resolves the winner after movement; suppress a payday that
        // otherwise occurs inside this port's combined mission/economy update.
        bool countdownExpired = HasCountdownExpired(now);
        if (Stage == MissionStage.Started && !countdownExpired && NextPaydayTime == 0)
            NextPaydayTime = now + PaydayInterval;     // fsmission.cpp:2253 (seeded at start + 60)
        if (Stage == MissionStage.Started && !countdownExpired && now >= NextPaydayTime)
        {
            double treasureElapsed = PaydayInterval + now - NextPaydayTime;
            if (AllowDevelopments && TreasureParameters.AllowDevelopments) // fsmission.cpp:2520
            {
                foreach (var side in Sides)
                {
                    long income = (long)(IncomePerPayday * (1.0f + side.Civilization.IncomeMoney))
                                + StationIncomeFor(side);         // fsmission.cpp:2536 + 2538-2546
                    income = (long)(income * ShowcaseIncomeMultiplier); // P703 opt-in pacing extension
                    side.Money += income;                          // fsmission.cpp:2536
                    Site.PaydayEvent(side, income);                // site seam → host payout
                }
            }
            RegenerateTreasures(now, treasureElapsed);
            NextPaydayTime += PaydayInterval;           // keep phase (fsmission.cpp:2519)
        }

        double deltaT = now - LastUpdate;
        if (deltaT >= 600.0)
            throw new InvalidOperationException(
                $"cannot advance {deltaT:R}s in one tick (> 10 min); missionigc.cpp:3148 asserts now - m_lastUpdate < 600.0f");

        // ── chunked: clusters update once per chunk ───────────────────────
        // n = 1 + floor(deltaT / increment); for any deltaT > 0, n >= 1. The
        // even split makes each chunk <= TimeIncrement, never equal everywhere.
        int n = 1 + (int)(deltaT / TimeIncrement);
        double startTime = LastUpdate;

        for (int i = 1; i <= n; i++)
        {
            double timeUpdate = startTime + deltaT * ((double)i / n);
            foreach (var sector in Sectors)
            {
                // P600 — per-sector tick timing + population. Guarded on
                // TickProfiler.Instance so that when the profiler is OFF the
                // "sector:<id>" string concat and the O(models) ship-count LINQ
                // are NOT evaluated — a disabled profiler must cost nothing
                // beyond a null check (verification: negligible when off).
                if (TickProfiler.Instance is not null)
                {
                    using (TickProfiler.Enter("sector:" + sector.Id))
                        sector.Update(timeUpdate);
                    TickProfiler.RecordSectorCounts(sector.Id, sector.Models.Count, sector.Models.Count(m => m is Ship));
                }
                else
                {
                    sector.Update(timeUpdate);
                }
                // SIM-0065 — native raises ClusterUpdateEvent once per cluster
                // only on the every-10th active started pass, immediately after
                // that pass's UpdateSeenBySide sweep (clusterIGC.cpp:347-357).
                // Starting-stage passes raise no event (the native block is
                // gated on bStarted). The sector records whether this pass was
                // one of those.
                if (sector.ClusterUpdateDue)
                    Site.ClusterUpdateEvent(sector);    // faithful site call (c++:357)
            }
            LastUpdate = timeUpdate;
        }

        // postcondition on the chunked path: the last chunk equals now.
        // LastUpdate == now holds structurally (i == n ⇒ timeUpdate == startTime + deltaT == now).
    }

    /// <summary>P443 — the per-side starting treasury and the <b>immediate first
    /// payout</b>, run once when the mission enters <c>Started</c>. Each side is
    /// seeded with <c>StartingMoneyPerSide × (fStartingMoney + civ.BonusMoney)</c>
    /// (<c>fsmission.cpp:1992-2001</c>; <c>missionigc.cpp:2697,2752</c> seed the
    /// constant slot with <c>c_fStartingMoney = 16000.0f</c>), then — right after
    /// seeding — the existing per-side payday payout is run for that side through
    /// the <em>same</em> seam the periodic payday uses
    /// (<see cref="IIgcSite.PaydayEvent"/> → the host's
    /// <c>DoPayday</c>/<c>OnPayday</c> distribution), mirroring native's
    /// <c>DoPayday()</c> call at the end of the start sequence
    /// (<c>fsmission.cpp:2278</c>). No second payout path is written: the host
    /// splits <see cref="Side.Money"/> by the side's player count, sends
    /// <c>FM_S_PAYDAY(114)</c>, and deducts — exactly as on a periodic payday.
    /// Called from the Started-transition site (<c>Server.CompleteStartCountdown</c>),
    /// i.e. only on a <em>real</em> start, so a side that later joins is seeded
    /// at the mission start. It also anchors <see cref="NextPaydayTime"/> at the
    /// real start (<c>now + SecondsBetweenPaydays</c>, fsmission.cpp:2253): the
    /// mission may have spent its countdown in <c>Starting</c> (arming it on the
    /// first Starting tick would make the first periodic payday 15s early), so
    /// the started-transition time is the authoritative "mission start".</summary>
    public void ApplyStartingMoneyAndInitialPayday(double now)
    {
        foreach (var side in Sides)
        {
            long seed = StartingMoneyFor(side);
            side.Money = seed;                 // assignment, fsmission.cpp:2004 (fresh side starts at 0)
            Site.PaydayEvent(side, seed);      // fsmission.cpp:2278 — immediate payout via the existing seam
        }
        NextPaydayTime = now + PaydayInterval;   // fsmission.cpp:2253 — cadence anchored at the real start
    }

    /// <summary>P443 — the starting-treasury seed for one side:
    /// <c>StartingMoneyPerSide × (fStartingMoney + civ.BonusMoney)</c>, or 0 when
    /// the multiplier is ≤ 0 (<c>fsmission.cpp:1992-2002</c>).</summary>
    private long StartingMoneyFor(Side side)
    {
        // The fStartingMoney ≤ 0 guard (fsmission.cpp:2001-2002), where a
        // non-positive multiplier yields 0. Taken through a local so the named
        // StartingMoneyMultiplier (a const) doesn't constant-fold the branch away.
        float multiplier = StartingMoneyMultiplier;
        if (multiplier <= 0) return 0;
        return (long)(StartingMoneyPerSide * (multiplier + side.Civilization.BonusMoney));
    }

    /// <summary>P443 — the station-income term of the payday formula
    /// (<c>fsmission.cpp:2538-2546</c>): Σ over every station runtime in the
    /// mission of <c>GetStationType()->GetIncome()</c> for the side's current
    /// ownership. Read from the live <see cref="Sector.StationRuntimes"/> (not a
    /// snapshot), so a station's <see cref="Station.Capture"/> re-siding and its
    /// destruction both fall out of current ownership automatically.</summary>
    private long StationIncomeFor(Side side)
    {
        long total = 0;
        foreach (var sector in Sectors)
            foreach (var station in sector.StationRuntimes)
                if (station.SideId == side.Id)
                    total += station.Type.Income;
        return total;
    }

    // P163 — runtime-allocated station ids for stations created mid-simulation
    // (the building-effect transformation). The port of native
    // pmission->GenerateNewStationID() (FedSrv.CPP:11397), sharing the same
    // monotonic-counter shape as the server's ship-id allocator (_nextShipId,
    // Server.cs:59). Started
    // comfortably above the low static station ids the map's OT_station records
    // carry so a fresh build can never shadow an assembled station's id.
    private int _nextStationId = 9187;

    /// <summary>Allocate a unique runtime station id — the port of native
    /// <c>pmission-&gt;GenerateNewStationID()</c> (<c>FedSrv.CPP:11397</c>),
    /// the allocator the native <c>BuildStation</c> uses for the station that
    /// replaces a built asteroid (<c>FedSrv.CPP:11397</c>:
    /// <c>ds.stationID = pmission-&gt;GenerateNewStationID()</c>). Mirrors
    /// the server's ship-id allocator <c>_nextShipId</c> (Server.cs:59) as a
    /// simple monotonically-increasing counter. Called by the building-effect
    /// resolver (<see cref="Sector.ResolveBuildingEffects"/>) when a build
    /// completes. A station id is only compared against other station ids in
    /// this port (RemoveStation, Sector.cs:202), so its distinctness from the
    /// shared ship-id space is not required — it just keeps the spaces tidy. A
    /// plain-text mirror of the server's <c>_nextShipId</c> (Server.cs:59).</summary>
    internal short GenerateNewStationID() => (short)(_nextStationId++);

    /// <summary>
    /// Place a <see cref="Ship"/> in the given <see cref="Sector"/>. Routes to
    /// <see cref="Sector.Add"/>, the port of <c>CclusterIGC::AddModel</c>; the
    /// mission exists so it can be given a ship to place at construction
    /// (task's wording) and own the relationship. Seeds the ship's model clock
    /// to the current placement sim-time (<see cref="LastUpdate"/>) via
    /// <see cref="Ship.InitClock"/> so a ship placed mid-simulation does not take
    /// one oversized first <c>dt</c> equal to the mission age (BOT.md latent
    /// bug; the C++ initialises <c>m_lastUpdate</c> at creation). Throws if the
    /// sector is not one of this mission's sectors.
    /// </summary>
    public void PlaceShip(Ship ship, Sector sector)
    {
        ArgumentNullException.ThrowIfNull(ship);
        ArgumentNullException.ThrowIfNull(sector);
        if (!Sectors.Contains(sector))
            throw new InvalidOperationException(
                $"sector '{sector.Name}' is not part of this mission; a ship can only be placed in a sector the mission owns");
        ship.InitClock(LastUpdate);     // seed to placement sim-time (BOT.md ship-clock bug)
        ship.Mission = this;            // the ship's back-ref to its mission (combat needs it)
        ship.Sector = sector;           // the ship's back-ref to its sector (weapon places projectiles there)
        sector.Add(ship);
    }

    /// <summary>
    /// Place a ship by sector id (shortcut for <see cref="PlaceShip(Ship, Sector)"/>).
    /// Throws if no sector has that id.
    /// </summary>
    public void PlaceShip(Ship ship, short sectorId)
    {
        var sector = Sectors.FirstOrDefault(s => s.Id == sectorId)
            ?? throw new InvalidOperationException($"no sector with id {sectorId} in this mission");
        PlaceShip(ship, sector);
    }

    /// <summary>
    /// Register a <b>runtime</b> <see cref="Asteroid"/> (a wrapped
    /// <see cref="DataAsteroidIGC"/> carrying its depletable ore pool) with the
    /// given <see cref="Sector"/>, so the mining pass
    /// (<see cref="Sector.ResolveMining"/>) can drain it. This is the synthetic
    /// injection entry the loopback mining tests use — the demo map
    /// (<c>XXV_simple.igc</c>) has <em>zero</em> asteroids (the boot summary's
    /// own <see cref="Sector.Asteroids"/> sum is genuinely 0), so a mining test
    /// constructs its own rock directly here rather than depending on the real
    /// map having one, matching how the ship tests synthesize their placement.
    /// The mineral is implicit: an asteroid wraps its static record and the
    /// model's only runtime state is the ore pool (<see cref="Asteroid.OreRemaining"/>),
    /// position coming from the record. Throws if the sector is not this
    /// mission's (same guard as <see cref="PlaceShip(Ship, Sector)"/>).
    /// </summary>
    public void PlaceAsteroid(Asteroid asteroid, Sector sector)
    {
        ArgumentNullException.ThrowIfNull(asteroid);
        ArgumentNullException.ThrowIfNull(sector);
        if (!Sectors.Contains(sector))
            throw new InvalidOperationException(
                $"sector '{sector.Name}' is not part of this mission; an asteroid can only be placed in a sector the mission owns");
        asteroid.Mission = this;
        asteroid.Sector = sector;
        sector.AttachAsteroid(asteroid);
    }

    /// <summary>
    /// Register a <b>runtime</b> <see cref="Station"/> (a wrapped
    /// <see cref="DataStationIGC"/> + its resolved <see cref="DataStationTypeIGC"/>
    /// carrying the live armor/shield fractions) with the given
    /// <see cref="Sector"/>, so the combat pass (<see cref="Sector.ResolveCombatCollisions"/>)
    /// can hit it and the tick can regenerate it. This is the synthetic
    /// injection entry the loopback station-combat tests use, mirroring
    /// <see cref="PlaceAsteroid"/>: the demo map's real stations are the
    /// production path (assembled in <see cref="Assemble"/>), and a test that
    /// needs full control over <see cref="Station.MaxArmor"/> /
    /// <see cref="Station.MaxShield"/> (for exact fraction assertions) builds its
    /// own. Throws if the sector is not this mission's (same guard as
    /// <see cref="PlaceShip(Ship, Sector)"/>).
    /// </summary>
    public void PlaceStation(Station station, Sector sector)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentNullException.ThrowIfNull(sector);
        if (!Sectors.Contains(sector))
            throw new InvalidOperationException(
                $"sector '{sector.Name}' is not part of this mission; a station can only be placed in a sector the mission owns");
        station.Mission = this;   // P475 — the synthetic/test station's back-ref (mirrors PlaceShip)
        sector.AttachStation(station);
        Sides.FirstOrDefault(s => s.Id == station.SideId)?.NotifyBuildingTechsChanged();
        Site.StationTypeChange(station); // Igc/stationIGC.cpp:72-75, after registration
    }

    /// <summary>
    /// P191 — port of <c>CFSMission::CheckForVictoryByStationKill</c>
    /// (<c>fsmission.cpp:3568-3710</c>), the station-based conquest/territory
    /// victory recompute, reduced to its two win-condition branches (the
    /// restart-station-elimination / <c>DeactivateSide</c> block at
    /// <c>:3695-3710</c> is NYI in native itself and out of scope here). Called
    /// by the sector after every station capture/kill ownership change; the
    /// caller forwards the returned values to the host so it can broadcast
    /// GAME_STATE(155) and declare the win via P178's GameOver path.
    /// </summary>
    /// <remarks>
    /// <para><b>Returns <c>(Winner, Changed)</c>.</b> <c>Winner</c> is the side
    /// that crossed its win threshold (conquest <see cref="ConquestWinThreshold"/>,
    /// territory <see cref="TerritoryWinThresholdPercent"/>), or <c>null</c>;
    /// <c>Changed</c> is the native <c>bChange</c> gate
    /// (<c>fsmission.cpp:3592,3649</c>): true iff at least one side's
    /// <c>ConquestPercent</c>/<c>TerritoryCount</c> actually changed, which
    /// decides whether the host broadcasts GAME_STATE at all.</para>
    /// <para><b>Stage/idempotency gate.</b> Native gates the whole function on
    /// <c>STAGE_STARTED</c> (or <c>STAGE_STARTING</c> with <c>bAllowEmptyTeams</c> —
    /// a separate empty-team policy) and on no winner yet decided (<c>m_psideWon == NULL</c>,
    /// <c>fsmission.cpp:3575-3578</c>). The host flips <see cref="Stage"/> to
    /// <see cref="MissionStage.Over"/> when it declares a winner, so the Started/
    /// Starting gate is also the idempotency guard: an ended mission never
    /// recomputes or re-broadcasts.</para>
    /// <para><b>Conquest branch</b> (<c>fsmission.cpp:3581-3625</c>): every
    /// station with <see cref="DataStationTypeIGC.HasCapability(short)"/> of
    /// <see cref="DataStationTypeIGC.SabmFlag"/> (except <paramref name="excluded"/>,
    /// the just-killed/captured station, per native <c>fsmission.cpp:3596</c>)
    /// is tallied per side; each side's <c>ConquestPercent = 100·owned/total</c>.
    /// Skipped wholesale when <see cref="ConquestEnabled"/> is false.</para>
    /// <para><b>Territory branch</b> (<c>fsmission.cpp:3634-3662</c>): runs only
    /// when <see cref="TerritoryEnabled"/> AND the conquest branch produced no
    /// winner. The exact native rule is applied: a cluster counts toward a
    /// side's <see cref="Side.TerritoryCount"/> iff every station in it (except
    /// <paramref name="excluded"/>) belongs to that same single side — a
    /// <b>strict unanimous-ownership</b> test, not a majority; a cluster with
    /// zero stations, or with stations split across two or more sides, counts
    /// for nobody. Threshold per native <c>nThreshold =
    /// (50 + nClusters·TerritoryWinThresholdPercent)/100</c>
    /// (<c>fsmission.cpp:3664-3666</c>).</para>
    /// <para><b>Capture timing / divergence from the task brief.</b> The brief
    /// said to exclude the just-<b>captured</b> station from the total. Verified
    /// against the live native source, capture does <em>not</em> exclude it:
    /// <c>CFSShip::CaptureStation</c> re-sides the station (<c>fsship.cpp:631</c>)
    /// <em>then</em> calls <c>CheckForVictoryByStationCapture</c> →
    /// <c>CheckForVictoryByStationKill(<b>NULL</b>, psideOld)</c>
    /// (<c>fsship.cpp:686</c>, <c>fsmission.cpp:3563-3565</c>) — <c>pstationKilled</c>
    /// is NULL on capture, so the freshly-captured station counts toward its new
    /// owner. Only the <b>kill</b> path passes the killed station
    /// (<c>FedSrv.CPP:12344</c>), which this port satisfies naturally because the
    /// dead station is already removed from its sector's runtime list before the
    /// recompute. The sector therefore calls this with <c>excluded = null</c> on
    /// capture (after re-siding) and with <c>null</c> on kill too (the station is
    /// already gone). Ownership is read from the live
    /// <see cref="Sim.Station.SideId"/>, which is re-sided on capture.</para>
    /// <para><b>Station universe.</b> The walk uses each sector's
    /// <see cref="Sector.StationRuntimes"/> — one runtime
    /// <see cref="Sim.Station"/> per static record whose station type resolves —
    /// the only stations carrying both a live <see cref="Sim.Station.SideId"/> and
    /// a resolved <see cref="DataStationTypeIGC"/> to test
    /// <see cref="DataStationTypeIGC.SabmFlag"/> against. This is the faithful
    /// stand-in for native's iteration over every mission station with a
    /// resolvable base type (<c>fsmission.cpp:3593-3600</c>); static-only
    /// records (unresolvable type) have no runtime station and are invisible to
    /// capture/destruction anyway, so excluding them matches native.</para>
    /// </remarks>
    public (Side? Winner, bool Changed) RecomputeConquestAndTerritory(Station? excluded)
    {
        if (IsTraining) return (null, false);
        // Native gate: only a Started (or Starting) mission with no winner yet
        // decided recomputes (fsmission.cpp:3575-3578). Started/Starting here
        // doubles as the idempotency guard — the host flips Stage to Over on a
        // win, and an Over mission never enters this branch.
        if (Stage != MissionStage.Started && !(Stage == MissionStage.Starting && AllowEmptyTeams))
            return (null, false);

        bool bChange = false;
        Side? psideWon = null;

        if (ConquestEnabled)
        {
            int nStationsTotal = 0;
            var nPerSide = new Dictionary<short, int>();

            // Count every Flag-capable station per side (fsmission.cpp:3593-3600);
            // excluded (the just-killed/captured) station is skipped (:3596).
            foreach (var sector in Sectors)
                foreach (var st in sector.StationRuntimes)
                {
                    if (ReferenceEquals(st, excluded)) continue;
                    if (!st.Type.HasCapability(DataStationTypeIGC.SabmFlag)) continue;
                    nStationsTotal++;
                    nPerSide[st.SideId] = nPerSide.GetValueOrDefault(st.SideId) + 1;
                }

            if (nStationsTotal != 0)                                    // fsmission.cpp:3604
            {
                foreach (var side in Sides)
                {
                    byte conquest = (byte)(100 * nPerSide.GetValueOrDefault(side.Id) / nStationsTotal);
                    if (conquest != side.ConquestPercent)              // fsmission.cpp:3613
                    {
                        bChange = true;                                // fsmission.cpp:3614
                        side.ConquestPercent = conquest;               // fsmission.cpp:3615
                        if (conquest >= ConquestWinThreshold)          // fsmission.cpp:3624
                            psideWon = side;
                    }
                }
            }
        }

        // FedSrv/fsmission.cpp:3628-3630: retain the rankings but suppress a
        // conquest winner with one active team when empty teams are allowed.
        if (ConquestEnabled && AllowEmptyTeams && Sides.Count(side => side.Active) == 1)
            psideWon = null;

        if (TerritoryEnabled && psideWon is null)                       // fsmission.cpp:3634
        {
            var nTerritories = new Dictionary<short, int>();

            foreach (var sector in Sectors)
            {
                Side? psideOwner = null;
                bool mixed = false;
                foreach (var st in sector.StationRuntimes)             // fsmission.cpp:3639-3644
                {
                    if (ReferenceEquals(st, excluded)) continue;
                    if (psideOwner is null)
                        psideOwner = Sides.FirstOrDefault(s => s.Id == st.SideId);
                    else if (psideOwner.Id != st.SideId) { mixed = true; break; }
                }
                if (psideOwner is not null && !mixed)                   // native: psideOwner && (psl == NULL) — walked off the end => unanimous
                    nTerritories[psideOwner.Id] = nTerritories.GetValueOrDefault(psideOwner.Id) + 1;
            }

            byte threshold = (byte)((50 + Sectors.Count * TerritoryWinThresholdPercent) / 100);  // fsmission.cpp:3663
            foreach (var side in Sides)
            {
                byte t = (byte)nTerritories.GetValueOrDefault(side.Id);
                if (t != side.TerritoryCount)                          // fsmission.cpp:3668
                {
                    bChange = true;                                    // fsmission.cpp:3669
                    side.TerritoryCount = t;                           // fsmission.cpp:3670
                    if (t >= threshold)                                // fsmission.cpp:3671-3673
                        psideWon = side;
                }
            }
        }

        // P844b/SIM-0531 — native's allies-override tail for
        // <c>CheckForVictoryByStationKill</c> (fsmission.cpp:3719-3743, Imago
        // ALLY 7/17/09): when every ACTIVE side is one alliance group the
        // alliance wins, overriding the conquest/territory tally. Native gates
        // this on <c>bAllies</c> — set only when the conquest branch detects a
        // side with an alliance (fsmission.cpp:3573/3615: <c>if (pside-&gt;
        // GetAllies() != NA) bAllies = true;</c>) — so a territory-only mission
        // (or a lone un-allied active side) never triggers it. The gate
        // preserves the KGJV #62 AllowEmptyTeams one-active-team suppression
        // above (fsmission.cpp:3628-3630): an un-allied single active side keeps
        // the game going, exactly as native's <c>bAllies == false</c> does,
        // while an allied active side still wins (native's documented
        // "allies override to prevent issues"). The override runs AFTER the
        // whole conquest/territory recompute, matching native's end-of-body
        // placement (:3742).
        if (ConquestEnabled && Sides.Any(s => s.AllyGroup is not null))
        {
            var allyWon = AllianceOverrideWinner(this);
            if (allyWon is not null)
                psideWon = allyWon;
        }

        return (psideWon, bChange);
    }

    /// <summary>
    /// P844b/SIM-0531/0533 — the pure allies-override winner, moved into Core
    /// so the Server's win-by-inactive-sides family and this conquest/territory
    /// recompute share one faithful port of native's double loop
    /// (fsmission.cpp:3719-3743 and its inactive-sides twin :3833-3855, Imago
    /// ALLY 7/17/09). Native walks every ordered pair of ACTIVE sides
    /// (<c>GetActiveF()</c>), keeping <c>bAllAllies</c> true unless some pair of
    /// distinct active sides fails <c>IsideIGC::AlliedSides</c>
    /// (<c>Igc/igc.h:4297-4305</c>: same side, or equal non-NA alliance ids —
    /// the port's <see cref="Side.AllyGroup"/>); when it stays true the winner
    /// is the last active side visited. With a single active side the only pair
    /// is itself, so <c>bAllAllies</c> stays true and that side wins — the
    /// edge <c>CheckForVictoryByInactiveSides</c> relies on when the player
    /// tally is empty (P844b/SIM-0533). Returns the alliance member (or lone
    /// active side), or null when the active sides are not one alliance / there
    /// are none (0 active sides => the loop body never runs => null).
    /// </summary>
    public static Side? AllianceOverrideWinner(Mission mission)
    {
        Side? allywinside = null;
        foreach (var otherside in mission.Sides)
        {
            if (!otherside.Active) continue;                    // :3729 GetActiveF()
            foreach (var thisside in mission.Sides)
            {
                if (!thisside.Active) continue;                 // :3735 GetActiveF()
                if (!AlliedSides(otherside, thisside) && !ReferenceEquals(otherside, thisside))
                    return null;                                // :3736 — not all one alliance
                allywinside = thisside;                         // :3739 — native keeps the last
            }
        }
        return allywinside;
    }

    /// <summary>P844b — the override's pairwise test (the port of native's
    /// <c>IsideIGC::AlliedSides</c>, <c>Igc/igc.h:4297-4305</c>, plus the
    /// active-side walk): true iff <paramref name="a"/> and
    /// <paramref name="b"/> are the same side or share one non-NA alliance id.
    /// Pure logic moved into Core for <see cref="AllianceOverrideWinner"/>.</summary>
    private static bool AlliedSides(Side a, Side b) =>
        ReferenceEquals(a, b) || (a.AllyGroup is { } g && g == b.AllyGroup);

    /// <summary>
    /// P302 — the port of the prosperity + artifact win conditions, the sibling
    /// of <see cref="RecomputeConquestAndTerritory"/> following the identical
    /// shape: a stage-gated recompute that returns <c>(Side? Winner, bool
    /// Changed)</c> for the host to broadcast and, on a winner, declare over via
    /// the shared P178 GameOver path. <b>Selection wiring:</b> like the P191
    /// flags, each branch runs only when its native goal parameter enables it — prosperity
    /// never fires alongside conquest/territory unless opted in, and artifacts
    /// likewise. The caller (the Server's development-completion and
    /// artifact-secure handlers) invokes this at the two real native trigger
    /// events.
    /// </summary>
    /// <remarks>
    /// <para><b>Prosperity branch.</b> The real native prosperity win is the
    /// completion of the special economic-victory development
    /// <c>c_didTeamMoney</c> (<see cref="TeamMoneyDevelopmentId"/>=1): native
    /// <c>EnterGame</c> creates a per-side bucket over that development priced
    /// at <c>fGoalTeamMoney × WinTheGameMoney</c> (<c>missionigc.h:770-794</c>)
    /// and <c>FedSrvSiteBase::DevelopmentCompleted</c> ends the game the moment
    /// that bucket completes (<c>FedSrv.CPP:11274-11279</c>). This branch is a
    /// state recompute over the same fact — a side whose
    /// <see cref="BucketItemType.BitDevelopment"/> bucket wrapping
    /// <see cref="TeamMoneyDevelopmentId"/> is <see cref="Bucket.Completed"/>
    /// has crossed the economic threshold and is the winner. (The strict
    /// fsmission.cpp:2395-2420 timeout-tiebreak variant is a separate
    /// countdown-ending decider, not this win condition.)</para>
    /// <para><b>Artifact branch.</b> The real native artifact win is scored
    /// when a side secures neutral artifacts: securing at one's own station a
    /// carried value equal to the <see cref="Side.ArtifactSentinelFlag"/>
    /// sentinel increments <see cref="Side.Artifacts"/>, then
    /// <c>CFSMission::CheckForVictoryByFlags</c> awards the game to the first
    /// side with <c>GetArtifacts() &gt;= nGoalArtifactsCount</c>
    /// (<c>fsmission.cpp:3888-3894</c>) — the artifact leg of
    /// <c>CheckForVictoryByFlags</c>, whose stage gate is STAGE_STARTED only
    /// (<c>fsmission.cpp:3864</c>, P844/SIM-0534). This branch recomputes that threshold
    /// against <see cref="ArtifactWinGoal"/>.</para>
    /// <para>The prosperity branch is guarded by the P191 stage gate
    /// (<c>Started</c>/<c>Starting</c>); the artifact leg is
    /// <c>Started</c>-only, per native CheckForVictoryByFlags
    /// (<c>fsmission.cpp:3864</c>) — the host flips the mission
    /// <see cref="MissionStage.Over"/> on a win, and an Over mission never
    /// re-enters.</para>
    /// </remarks>
    public (Side? Winner, bool Changed) RecomputeProsperityAndArtifacts()
    {
        if (Stage is not (MissionStage.Started or MissionStage.Starting))
            return (null, false);

        Side? psideWon = null;

        // Prosperity (FedSrv.CPP:11274-11279): completing the team-money
        // development is the economic-threshold win.
        if (ProsperityEnabled)
        {
            foreach (var side in Sides)
            {
                var teamMoney = side.Buckets.FirstOrDefault(b =>
                    b.ItemType == BucketItemType.BitDevelopment
                    && b.Development is { } d && d.DevelopmentId == TeamMoneyDevelopmentId);
                if (teamMoney is { Completed: true })
                {
                    psideWon = side;
                    break;
                }
            }
        }

        // Artifacts (fsmission.cpp:3890): first side to secure the goal count —
        // the artifact leg of CheckForVictoryByFlags, whose stage gate is
        // STAGE_STARTED only (fsmission.cpp:3864, P844/SIM-0534): unlike the
        // Starting-tolerant station-kill recompute (:3575-3578), a Starting
        // mission never wins by flags/artifacts in native.
        if (ArtifactEnabled && psideWon is null && Stage == MissionStage.Started)
        {
            foreach (var side in Sides)
                if (side.Artifacts >= ArtifactWinGoal)
                {
                    psideWon = side;
                    break;
                }
        }

        return (psideWon, psideWon is not null);
    }

    /// <summary>
    /// P303 — the port of the <b>flags + kills</b> win conditions, the sibling of
    /// <see cref="RecomputeProsperityAndArtifacts"/> following the identical
    /// shape: a stage-gated recompute that returns <c>(Side? Winner, bool
    /// Changed)</c> for the host to broadcast and, on a winner, declare over via
    /// the shared P178 GameOver path. <b>Selection wiring:</b> like the P191/P302
    /// flags, each branch runs only when its native goal parameter enables it — flags never
    /// fires alongside conquest/territory unless opted in, and kills likewise.
    /// The caller (the Server's flag-secure and kill-credit handlers) invokes
    /// this at the two real native trigger events.
    /// </summary>
    /// <remarks>
    /// <para><b>Flags branch.</b> The real native flags win is scored when a
    /// side secures captured enemy flags: securing at one's own station a
    /// carried value that is a real enemy side's id increments
    /// <see cref="Side.Flags"/> (<c>FedSrv.CPP:13136</c>), then the flag leg of
    /// <c>CFSMission::CheckForVictoryByFlags</c> awards the game to the first
    /// side with <c>GetFlags() &gt;= nGoalFlagsCount</c>
    /// (<c>fsmission.cpp:3883-3887</c>; the <c>sidFlag != SIDE_TEAMLOBBY</c>
    /// real-flag branch — the team-lobby sentinel is the artifact leg, already
    /// built in <see cref="RecomputeProsperityAndArtifacts"/>). This branch
    /// recomputes that threshold against <see cref="FlagsWinGoal"/>.</para>
    /// <para><b>Kills branch.</b> The real native kills win is the deathmatch
    /// kill-threshold win: a side that earns a credited kill (<c>psideCredit-&gt;
    /// AddKill()</c>, <c>FedSrv.CPP:11868</c>) is tested by
    /// <c>CFSMission::CheckForVictoryByKills</c> and wins with <c>GetKills() &gt;=
    /// nGoalTeamKills</c> (<c>fsmission.cpp:3749-3760</c>; native
    /// <c>IsDeathMatchGame()</c> ⇔ <c>nGoalTeamKills != 0</c>, igc.h:1521-1523).
    /// This branch recomputes that threshold against <see cref="KillsWinGoal"/>.
    /// (The countdown-expiry tie-break fallback that also uses kills,
    /// <c>fsmission.cpp:2468-2492</c>, is a separate time-expired decider, not
    /// this win condition.)</para>
    /// <para>Both branches are guarded by native's STAGE_STARTED-only gate
    /// (fsmission.cpp:3751/3864 — P844/SIM-0532/0534): the host flips the
    /// mission <see cref="MissionStage.Over"/> on a win, and an Over mission
    /// never re-enters.</para>
    /// </remarks>
    public (Side? Winner, bool Changed) RecomputeFlagsAndKills()
    {
        // Native stage gate: STAGE_STARTED only, with no Starting allowance —
        // CheckForVictoryByKills (fsmission.cpp:3751) and CheckForVictoryByFlags
        // (fsmission.cpp:3864) both test `STAGE_STARTED == GetStage()` plus
        // `m_psideWon == NULL`; the port's Over flip in DeclareGameOver covers
        // the no-winner-yet half. (P844/SIM-0532/0534.)
        if (Stage != MissionStage.Started)
            return (null, false);

        Side? psideWon = null;

        // Flags (fsmission.cpp:3883): first side to secure the goal count of
        // captured enemy flags.
        if (FlagsEnabled)
        {
            foreach (var side in Sides)
                if (side.Flags >= FlagsWinGoal)
                {
                    psideWon = side;
                    break;
                }
        }

        // Kills (fsmission.cpp:3756): first side to score the deathmatch goal.
        if (KillsEnabled && psideWon is null)
        {
            foreach (var side in Sides)
                if (side.Kills >= KillsWinGoal)
                {
                    psideWon = side;
                    break;
                }
        }

        return (psideWon, psideWon is not null);
    }

    /// <summary>
    /// P259 — whether <paramref name="side"/> currently sees cluster
    /// <paramref name="clusterId"/>: the port of the original native
    /// <c>CFSSide::CanSeeCluster(clusterID)</c> cluster-visibility test
    /// (sideigc.cpp — see the P259 writeup; removed from the on-disk
    /// FreeAllegiance tree). A cluster is visible to a side iff the side owns a
    /// live ship, station <b>or probe</b> in a cluster that is <b>within
    /// <see cref="Side.VisibilityRange"/> of it on the strategic map</b> —
    /// presence in the cluster itself is the trivial distance-zero case (a side
    /// physically occupying a cluster always sees it). The probe branch is the
    /// P260 probe-visibility exception: a deployed probe is a real, damageable
    /// asset (<c>c_mtDamagable</c>/<c>c_mtScanner</c>, <c>probeigc.cpp:115</c>)
    /// whose reveal lasts exactly as long as it survives (a destroyed/expired
    /// probe leaves <c>Sector.Models</c>, so the live query below drops it).
    /// See <see cref="CanSideSeeCluster"/> remarks for the probe-specific
    /// sourcing.
    /// </summary>
    /// <remarks>
    /// <para><b>Probe exception sourcing (P260).</b> The original-MS-native
    /// <c>CFSSide::CanSeeCluster</c> the brief cites (``side owns a live probe in
    /// this cluster``) is not in the on-disk tree. The real modern-FA probe
    /// mechanic is <c>CprobeIGC::InScannerRange</c>
    /// (<c>src/Igc/probeigc.h:163-191</c>): a probe is a per-object scanner,
    /// <b>confined to its own cluster</b> (<c>pModel-&gt;GetCluster() ==
    /// pcluster</c>) and <b>limited by its real ScannerRange</b> from its own
    /// deployed position (<c>scannerRange × signature × c_gaScanRange /
    /// c_gaSignature</c>, plus line-of-sight) — i.e. the reveal is radar-limited,
    /// <b>not</b> a blanket cluster-wide uncover. At the port's cluster-
    /// abstraction level that manifests as the presence case: a live probe owned
    /// by the side and physically occupying the cluster grants visibility of it
    /// (distance-zero), riding the same strategic-range proxy every owned asset
    /// shares. A probe never scans across clusters (the native
    /// <c>InScannerRange</c> gate), so the reveal is anchored to the cluster it
    /// sits in, and it lasts exactly as long as the probe survives (a destroyed
    /// or expired probe is removed from <c>Sector.Models</c> — see
    /// <c>Probe.ReceiveDamage</c> and <c>Probe.OnUpdate</c> — so the live query
    /// below drops it immediately).</para>
    /// <para><b>Asset universe.</b> The side's live ships are drawn from every
    /// sector's <see cref="Sector.Models"/> (a <see cref="Ship"/> with the
    /// side's id, not dead); its stations from every sector's
    /// <see cref="Sector.StationRuntimes"/> (same side, not dead); its live
    /// probes from every sector's <see cref="Sector.Models"/> (a <see cref="Probe"/>
    /// with the side's id, not dead). Docked ships
    /// still count (they are still models in the sector), matching the "side
    /// physically occupy a ship/station in that cluster" rule.</para>
    /// <para><b>Distance-to-cluster proxy (stated).</b> The port's clusters carry
    /// no in-world bounding geometry (<see cref="DataClusterIGC"/> has only
    /// screen coordinates), and this port has already established that raw 3D
    /// positions are not comparable across cluster frames (P158: "cross-frame
    /// distances meaningless" — the pathfinder uses hop count, not geodesic).
    /// So the cross-cluster "within range" distance is measured on the
    /// <b>strategic map</b>: the screen distance between two clusters'
    /// <see cref="DataClusterIGC.ScreenX"/>/<see cref="DataClusterIGC.ScreenY"/>,
    /// the same 2D layout a "map that can see to some range / radar" (the
    /// original mechanic Domingo described) would operate in. An asset in
    /// cluster X reveals cluster C iff that screen distance ≤
    /// <see cref="Side.VisibilityRange"/>. This is a stated proxy (the original
    /// native 3D metric is unverifiable from the on-disk tree) — see the P259
    /// writeup.</para>
    /// </remarks>
    public bool CanSideSeeCluster(Side side, short clusterId)
    {
        ArgumentNullException.ThrowIfNull(side);

        var target = Sectors.FirstOrDefault(s => s.Id == clusterId);
        if (target is null) return false;

        float rangeSq = side.VisibilityRange * side.VisibilityRange;
        foreach (var sector in Sectors)
        {
            // An owned asset in this sector can reveal the target iff this
            // sector is within VisibilityRange of the target on the strategic
            // map. Presence in the target itself is the distance-zero case
            // (same sector ⇒ screen distance 0 ≤ any non-negative range).
            if (!ScreenWithinRange(sector, target, rangeSq)) continue;

            foreach (var m in sector.Models)
            {
                if (m is not Ship ship) continue;
                if (ship.SideId != side.Id) continue;
                if (ship.Dead) continue;
                return true;
            }

            foreach (var st in sector.StationRuntimes)
            {
                if (st.SideId != side.Id) continue;
                if (st.Dead) continue;
                return true;
            }

            // P260 — the probe-visibility exception: a live probe owned by the
            // side counts as an owned asset exactly like a ship/station (a probe
            // is a real c_mtScanner/damageable model sitting in its cluster). A
            // destroyed or expired probe is no longer in sector.Models, so it
            // drops out of this live query and the reveal clears with it.
            // MUST-FAIL-PROVEN: with this branch disabled the 5 probe-visibility
            // tests go red against the pre-exception behavior, then green here.
            foreach (var m in sector.Models)
            {
                if (m is not Probe probe) continue;
                if (probe.SideId != side.Id) continue;
                if (probe.Dead) continue;
                return true;
            }
        }

        return false;
    }

    /// <summary>P259 — whether two sectors are within <paramref name="rangeSq"/>
    /// of each other on the strategic map (screen-coordinate distance, squared to
    /// avoid a square root). See <see cref="CanSideSeeCluster"/>.</summary>
    private static bool ScreenWithinRange(Sector a, Sector b, float rangeSq)
    {
        float dx = a.Cluster.ScreenX - b.Cluster.ScreenX;
        float dy = a.Cluster.ScreenY - b.Cluster.ScreenY;
        return dx * dx + dy * dy <= rangeSq;
    }

    /// <summary>
    /// P259 — compute the set of clusters <paramref name="side"/> currently sees,
    /// using <see cref="CanSideSeeCluster"/> per cluster. This is the real
    /// per-side computation both the on-demand <see cref="Side.CanSeeCluster"/>
    /// and the periodic <see cref="RefreshSideVisibility"/> share.
    /// </summary>
    public IReadOnlySet<short> ComputeVisibleClusters(Side side)
    {
        ArgumentNullException.ThrowIfNull(side);

        var visible = new HashSet<short>();
        foreach (var sector in Sectors)
            if (CanSideSeeCluster(side, sector.Id))
                visible.Add(sector.Id);
        return visible;
    }

    /// <summary>
    /// P259 — recompute every side's visible-cluster set from the current live
    /// ship/station state and stash it on <see cref="Side.VisibleClusters"/>.
    /// Driven periodically by <see cref="Update"/> (every
    /// <see cref="VisibilityRefreshTicks"/> Updates) and callable directly for
    /// a synchronous refresh. Pure simulation state — nothing is broadcast
    /// (the probe exception and any client-facing surface are later tasks).
    /// </summary>
    public void RefreshSideVisibility()
    {
        foreach (var side in Sides)
            side.VisibleClusters = ComputeVisibleClusters(side);
    }

    /// <summary>P259 — whether a point lies within <paramref name="range"/> of
    /// the <em>nearest warp</em> of <paramref name="target"/> (the stated
    /// distance-to-cluster proxy; see <see cref="CanSideSeeCluster"/>).</summary>
    private static bool AssetWithinRangeOfCluster(Vector3 pos, Sector target, float range)
    {
        if (range < 0f) return false;
        float rangeSq = range * range;
        foreach (var w in target.Warps)
        {
            var wp = w.Warp.Position;
            if (wp is not { Length: 3 }) continue;
            float dx = pos.X - wp[0];
            float dy = pos.Y - wp[1];
            float dz = pos.Z - wp[2];
            if (dx * dx + dy * dy + dz * dz <= rangeSq) return true;
        }
        return false;
    }
}
