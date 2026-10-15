namespace Allegiance.Core.Model;

using Allegiance.Core.Sim;

/// <summary>
/// A minimal <b>side (team)</b> of a static <see cref="Mission"/>: its id, its
/// name (when the host supplied one), its civilization resolved from the
/// core, and its accrued treasury. Buckets and tech masks are runtime concerns
/// built by the engine's <c>GenerateMission</c> over the same (id,
/// civilization) skeleton (<c>missionigc.cpp:3734-3899</c>); <see cref="Money"/>
/// is the one runtime piece the payday slice (P51) owns.
/// </summary>
/// <remarks>
/// The <see cref="ISideIgc"/> marker lets the tick report the side pass to the
/// observation hook (<see cref="IIgcSite.SideUpdated"/>). <see cref="Update"/>
/// is a recording stub: the C++ <c>CsideIGC::Update</c> (<c>sideigc.h:41</c>)
/// refreshes its buckets and advances <c>m_lastUpdate</c>; ours just counts the
/// tick and remembers the time. <see cref="Money"/> is the port of the C++
/// FedSrv accumulator <c>m_rgMoney[sideID]</c> (<c>fsmission.cpp:61,3504</c> —
/// initialised to 0 at mission start), fed by the tick's payday accrual
/// (<c>fsmission.cpp:2536</c>) and drained by the host's per-player payout
/// (<c>fsmission.cpp:2301-2309</c>).
/// </remarks>
public sealed class Side : ISideIgc
{
    /// <summary>The side id (<c>sideID</c>).</summary>
    public short Id { get; }

    /// <summary>
    /// P363 — the side's RGBA colour (floats in 0..1), the port of the native
    /// per-side <c>m_data.color</c> (<c>DataSideIGC.color</c>, igc.h:2064;
    /// <c>GetColor()</c> returns it, sideigc.h:369). Native assigns each side its
    /// colour by <b>side id</b> from a fixed six-slot palette in the mission
    /// setup (<c>UpdateSides</c>, missionigc.cpp:3734-3746:
    /// <c>ds.color.SetRGBA(sideColors[sid][0..2])</c>), NOT from the
    /// civilization — so the colour is a static property of the side slot for
    /// the whole mission. This is the value native hands to the ship trail
    /// (<c>pThingSite-&gt;SetTrailColor(pside-&gt;GetColor())</c>,
    /// shipIGC.cpp:149 / trekigc.cpp:4419), the endgame screen
    /// (<c>fsmission.cpp:3073</c>) and the builder-beam colour
    /// (buildingeffectigc.h:77). Seeded by <see cref="Mission.Assemble"/> from
    /// <see cref="SidePalette.ColorFor"/>, defaulting to the neutral gray native
    /// uses for an absent side.</summary>
    public float ColorR { get; set; } = 0.5f;
    public float ColorG { get; set; } = 0.5f;
    public float ColorB { get; set; } = 0.5f;
    public float ColorA { get; set; } = 1f;

    /// <summary>The side name, if the supplying host provided one; else <c>null</c>.</summary>
    public string? Name { get; }

    /// <summary>The civilization backing this side, resolved from the core.
    /// Settable since P186: <c>CHANGE_TEAM_CIV(51)</c> swaps it mid-mission
    /// (<c>fsmission.cpp:5268-5284 SetSideCiv</c>). Making it writable is safe
    /// because nothing on this side is keyed to the civilization — the
    /// construction-bucket catalog (<see cref="BucketCatalog.ForSide"/>) is a
    /// single fixed synthetic bucket that does not depend on the civ (it
    /// references hull 504 directly, Bucket.cs:242-258), so no bucket
    /// recomputation is needed on a civ swap; the only consumers read
    /// <c>Lifepod</c>/<c>IncomeMoney</c>/<c>CivilizationId</c> live.</summary>
    public DataCivilizationIGC Civilization { get; set; }

    /// <summary>
    /// The side's construction buckets (P119) — the things the team can fund to
    /// build. Seeded by <see cref="Mission.Assemble"/> from the (synthetic,
    /// real-hull-backed) <see cref="BucketCatalog"/>; advanced for build time in
    /// <see cref="Update"/>. The port of the native per-side
    /// <c>BucketListIGC m_buckets</c> (sideigc.h:329).</summary>
    public IReadOnlyList<Bucket> Buckets { get; }

    /// <summary>
    /// The side's persistent <b>development</b> tech tree (P189) — the port of
    /// the native per-side <c>m_data.ttbmDevelopmentTechs</c>
    /// (<c>sideigc.h:172-185</c>, one of the two independent tech sources that
    /// <c>GetTechs()</c> ORs together, <c>sideigc.h:133-136</c>; the other is
    /// <see cref="BuildingTechs"/>, P444). Seeded at
    /// construction from the civilization's base techs, exactly as the native
    /// side initialises them (<c>m_data.ttbmDevelopmentTechs =
    /// GetCivilization()-&gt;GetBaseTechs()</c>, <c>sideigc.cpp:102</c> /
    /// <c>DestroyBuckets</c>). Grown by
    /// <see cref="Server"/>'s development-bucket completion (the port of
    /// <c>ApplyDevelopmentTechs</c>, <c>sideigc.h:194-210</c>): only when
    /// newly-introduced bits exist are they OR-ed in and the side notified via
    /// <c>SIDE_TECH_CHANGE(75)</c>.</summary>
    public TechTreeBitMask DevelopmentTechs { get; set; }

    /// <summary>Stable catalog seed, native UpdateInitialTechs (Igc/sideigc.h:219-222).
    /// Captured after mission path permissions and before any research/test grants.</summary>
    public TechTreeBitMask InitialTechs { get; internal set; }

    /// <summary>
    /// P444 — the side's <b>building</b> techs, the port of the native
    /// per-side <c>m_data.ttbmBuildingTechs</c> (<c>sideigc.h:172-185</c>), the
    /// OR of every owned station type's <c>EffectTechs</c>. This is how owning
    /// a Supremacy / Tactical / Expansion / Shipyard base makes that tier's
    /// hulls buyable <b>at any of the side's stations</b> and its developments
    /// researchable — the core reason a techbase matters side-wide. Native
    /// keeps it current on <c>AddStation</c> (OR in the new station's
    /// <c>GetStationType()-&gt;GetEffectTechs()</c>, <c>sideigc.h:274-280</c>)
    /// and recomputes it from scratch on <c>DeleteStation</c> through
    /// <c>ResetBuildingTechs</c> (<c>sideigc.h:142-158,282-287</c>).
    /// <para>This port chooses <b>recompute-on-access</b> over the native's
    /// incremental-add / recompute-on-remove bookkeeping: <c>Side</c> here does
    /// not own a station list (stations live in <c>Sector.StationRuntimes</c>,
    /// keyed by side id), so a maintained cache on the side would need a hook
    /// at every mutation site (<see cref="Mission.PlaceStation"/>,
    /// <see cref="Model.Sector.RemoveStation"/>, <see cref="Station.Capture"/>)
    /// and would risk going stale. Recomputation walks the mission's live
    /// stations each read — the port's analogue of native's
    /// recompute-from-scratch <c>ResetBuildingTechs</c> — and is cheap (a
    /// mission holds at most dozens of stations), so placement / destruction /
    /// capture are all reflected automatically with no cache to invalidate.
    /// For a mission-less side (isolated test) it reads as empty.</summary>
    public TechTreeBitMask BuildingTechs
    {
        get
        {
            var acc = TechTreeBitMask.Empty;
            if (Mission is not { } m) return acc;
            foreach (var sector in m.Sectors)
                foreach (var station in sector.StationRuntimes)
                    if (station.SideId == Id)
                        acc = acc.Or(TechTreeBitMask.FromBytes(station.Type.TtbmEffects));
            return acc;
        }
    }

    /// <summary>
    /// P444 — the side's combined tech mask
    /// <c>BuildingTechs | DevelopmentTechs</c>, the port of the native
    /// <c>side.GetTechs() = ttbmBuildingTechs | ttbmDevelopmentTechs</c>
    /// (<c>sideigc.h:133-136</c>) that <c>station.CanBuy</c> consults
    /// (<c>stationIGC.h:383-391</c>). The single term the server's buy-gate
    /// (P283 <c>StationSellsHull</c> and P441's BUY_LOADOUT gate) now uses.</summary>
    public TechTreeBitMask Techs => BuildingTechs.Or(DevelopmentTechs);

    /// <summary>
    /// P551 — whether this side may buy (fund) <paramref name="bucket"/> — the
    /// port of the native <c>IsideIGC::CanBuy(const IbuyableIGC*)</c>
    /// (<c>src/Igc/sideigc.h:224-281</c>), which unwraps a bucket to its
    /// buyable and decides:
    ///   • OT_development: buyable iff the buyable's <c>GetRequiredTechs()</c>
    ///     is a subset of the side's techs (<c>ttbmSide</c>) OR it is the
    ///     <c>c_didTeamMoney</c> development (sideigc.h:246-249);
    ///   • OT_stationType: not buyable while a <b>successor</b> station type is
    ///     available (the obsolete cut), else buyable iff some owned station's
    ///     local techs ∪ the side's techs cover the required techs
    ///     (sideigc.h:250-265);
    ///   • OT_droneType (and the port's ship-type bucket, whose buyable is a
    ///     drone hull): buyable iff some owned station's local techs ∪ the
    ///     side's techs cover the required techs (sideigc.h:265-280).
    /// <c>ttbmSide</c> is <see cref="Techs"/> (the native
    /// <c>m_ttbmBuildingTechs | m_data.ttbmDevelopmentTechs</c>,
    /// sideigc.h:237). Native feeds this gate into the bucket-donation spend
    /// (<c>if (pside->CanBuy(pbucket))</c>, FedSrv.CPP:7152); a side that
    /// cannot buy a bucket therefore cannot invest in it (a donation is
    /// refunded in full instead).</summary>
    public bool CanBuy(Bucket bucket)
    {
        if (bucket is null) return false;
        var ttbmSide = Techs;   // sideigc.h:237 (m_ttbmBuildingTechs | m_data.ttbmDevelopmentTechs)
        switch (bucket.ItemType)
        {
            case BucketItemType.BitDevelopment:
            {
                if (bucket.Development is not { } dev) return false;
                // sideigc.h:246-249 — non-localized: buyable on global tech bits
                // alone, plus the team-money shortcut (c_didTeamMoney).
                if (dev.DevelopmentId == Mission.TeamMoneyDevelopmentId) return true;
                return TechTreeBitMask.FromBytes(dev.TtbmRequired).IsSubsetOf(ttbmSide);
            }
            case BucketItemType.BitStationType:
            {
                if (bucket.StationType is not { } st) return false;
                if (Mission is not { } sm || sm.Core is not { } sCore) return false;
                // sideigc.h:250-263 — a station type whose successor is
                // available (not obsolete) cannot be bought.
                if (EffectiveSuccessor(st, DevelopmentTechs, sCore).StationTypeId != st.StationTypeId)
                    return false;
                return AnyStationBuilds(st.TtbmRequired, ttbmSide);
            }
            case BucketItemType.BitDroneType:
            {
                if (bucket.DroneType is not { } dt) return false;
                return AnyStationBuilds(dt.TtbmRequired, ttbmSide);   // sideigc.h:265-280
            }
            case BucketItemType.BitShipType:
            {
                // The ship-type bucket wraps a drone hull as its buyable, so the
                // generic drone-type branch applies (sideigc.h:265-280).
                if (Mission is not { } mm || mm.Core is not { } cCore
                    || !cCore.HullTypes.TryGetValue(bucket.HullIdDrone, out var hull)) return false;
                return AnyStationBuilds(hull.TtbmRequired, ttbmSide);
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// P551 — the station-type <b>obsolete</b> cut: walk the successor chain,
    /// returning the effective (deepest non-superseded) station type — the port
    /// of native <c>IstationTypeIGC::GetSuccessorStationType(const IsideIGC*)</c>
    /// (<c>src/Igc/stationtypeigc.h:185-196</c>). A successor is taken when its
    /// required techs are a subset of <c>this.required ∪ side development
    /// techs</c> (note: native uses development techs here, not the full techs —
    /// <c>stationtypeigc.h:192-193</c>), recursing into the successor. If the
    /// returned type differs from <paramref name="st"/>, <paramref name="st"/>
    /// is obsolete and not buyable (<c>sideigc.h:252-254</c>).
    /// </summary>
    private static DataStationTypeIGC EffectiveSuccessor(
        DataStationTypeIGC st, TechTreeBitMask devTechs, StaticCore core)
    {
        if (core.StationTypes.TryGetValue(st.SuccessorStationTypeId, out var succ)
            && TechTreeBitMask.RequiredTechsBuyable(succ.TtbmRequired, st.TtbmRequired, devTechs))
            return EffectiveSuccessor(succ, devTechs, core);
        return st;
    }

    /// <summary>
    /// P551 — the generic station/drone/ship branch of <c>CanBuy</c>: true when
    /// some station owned by this side has <c>localTechs ∪ ttbmSide</c> covering
    /// the buyable's required techs — the port of the native station loop
    /// (sideigc.h:265-280, <c>ttbmStation = localTechs | ttbmSide</c>).</summary>
    private bool AnyStationBuilds(byte[] need, TechTreeBitMask ttbmSide)
    {
        if (Mission is not { } m) return false;
        foreach (var sector in m.Sectors)
            foreach (var station in sector.StationRuntimes)
                if (station.SideId == Id
                    && TechTreeBitMask.RequiredTechsBuyable(need, station.Type.TtbmLocal, ttbmSide))
                    return true;
        return false;
    }

    /// <summary>
    /// The side's global attribute multiplier set (P189) — the port of the
    /// native per-side <c>m_data.gasAttributes</c>
    /// (<c>sideigc.h:332-352</c>). Seeded at construction from the
    /// civilization's base gas attributes (<c>GetGlobalAttributeSet()</c> →
    /// <c>m_data.gasBaseAttributes</c>, <c>civilizationigc.h:73</c>). Accumulated
    /// by each completed non-tech-only development's <c>Gas</c> via
    /// <see cref="GlobalAttributeSet.Apply"/> (the native multiply,
    /// <c>igc.h:935-946</c>) — the port of
    /// <c>ApplyGlobalAttributeSet</c> (<c>sideigc.h:343-345</c>), which also
    /// triggers the whole-mission <c>SIDE_ATTRIBUTE_CHANGE(76)</c> broadcast.
    /// The 25 named factors' gameplay consumers are out of scope (store/broadcast
    /// only — see the stage report).</summary>
    public GlobalAttributeSet GlobalAttributes { get; set; }

    /// <summary>Number of times <see cref="Update"/> has been called.</summary>
    public long UpdateCount { get; private set; }

    /// <summary>The time of the most recent <see cref="Update"/>, or 0 before the first.</summary>
    public double LastUpdate { get; private set; }

    /// <summary>
    /// The side's accrued treasury, the port of <c>m_rgMoney[sideID]</c>
    /// (<c>fsmission.cpp:61,3504</c>: starts 0; <c>fsmission.cpp:2536</c> adds
    /// income each payday; <c>fsmission.cpp:2309</c> subtracts <c>delta·np</c>
    /// when the host pays out). A <c>long</c> here (vs the native 32-bit
    /// <c>Money</c>) is a deliberate widening so the sim accumulator cannot
    /// overflow for unrealistically long sessions; the wire field stays
    /// native-width 32-bit (see <see cref="Allegiance.Core.Wire.Payday"/>).
    /// Owned by <see cref="Mission.Update"/>'s payday pass and drained by the
    /// host's <c>PaydayEvent</c> handler on the same (sim) thread.
    /// </summary>
    public long Money { get; set; }

    /// <summary>
    /// Whether this side is currently marked aggregate-ready (P180) — the
    /// port of the native per-side <c>m_misdef.rgfReady[iSide]</c> flag that
    /// the server recomputes on every <c>PLAYER_READY</c> change and reports
    /// to the whole mission via <c>TEAM_READY(46)</c> when it changes
    /// (<c>fsmission.cpp:5449-5471</c>). Starts <c>false</c>; only the ready-up
    /// recompute (Server-side, <c>CheckForSideAllReady</c> analogue) writes
    /// it. Follows the same direct-field-on-Side shape as <see cref="Money"/> —
    /// this port stores per-side state on the side itself rather than in a
    /// side-keyed dictionary on <see cref="Mission"/>.
    /// </summary>
    public bool Ready { get; set; }

    /// <summary>
    /// Whether this side's readiness is being <b>forced</b> by its team leader
    /// (P184) — the port of the native per-side <c>m_misdef.rgfForceReady[iSide]</c>
    /// flag (<c>fsmission.cpp</c>) that <c>FORCE_TEAM_READY(47)</c> toggles. A
    /// persistent per-side flag, not a one-shot action: while <c>true</c>, the
    /// side's aggregate readiness is reported ready regardless of individual
    /// <c>PLAYER_READY</c> states (bypassing the per-player check in
    /// <c>Server.RecomputeSideReady</c>, matching native
    /// <c>CheckForSideAllReady</c>'s force-ready branch, <c>fsmission.cpp:5508-5509</c>);
    /// turning it off re-runs the normal per-player check. Starts <c>false</c>.
    /// Follows the same direct-field-on-Side shape as <see cref="Ready"/>.
    /// </summary>
    public bool ForceReady { get; set; }

    /// <summary>
    /// Whether this side auto-accepts pending side-join requests without
    /// leader approval (P186) — the port of the native per-side
    /// <c>m_misdef.rgfAutoAccept[sideID]</c> flag (<c>fsmission.cpp</c>), toggled
    /// by <c>AUTO_ACCEPT(52)</c> (<c>fsmission.cpp:4974-5011 SetAutoAccept</c>).
    /// While <c>true</c>, a <c>POSITIONREQ</c> for this side seats the requester
    /// immediately instead of routing it to the leader, and turning it <b>on</b>
    /// instantly seats any player with a currently-pending request on the side.
    /// Starts <c>false</c>. Follows the same direct-field-on-Side shape as
    /// <see cref="Ready"/>.</summary>
    public bool AutoAccept { get; set; }

    /// <summary>
    /// The side's alliance-group number (P190) — the port of the native
    /// per-side <c>m_misdef.rgfAllies[sideID]</c> slot
    /// (<c>fsmission.cpp:5288-5291 SetSideAllies</c>). <c>null</c> means this
    /// side has no alliance (<c>NA</c>, <c>igc.h:471</c>); otherwise two sides
    /// are allied iff they carry the <b>same</b> group value — a small
    /// <c>0..2</c> index into the alliance-group table computed by the server's
    /// <c>UpdateAlliances</c> port (<c>fsmission.cpp:5638-5811</c>). Starts
    /// <c>null</c>. Written only by the server's <c>CHANGE_ALLIANCE(196)</c>
    /// handler, which also broadcasts the whole mission's <c>Allies[]</c> array
    /// as <c>CHANGE_ALLIANCES(197)</c>. Follows the same direct-field-on-Side
    /// shape as <see cref="Ready"/>.</summary>
    public sbyte? AllyGroup { get; set; }

    /// <summary>
    /// Whether this side is <b>active</b> (P202) — the port of the native
    /// per-side <c>m_misdef.rgfActive[sideID]</c> slot
    /// (<c>fsmission.cpp:5300-5306</c> SetSideActive/GetSideActive), toggled
    /// by the pre-game roster-shrinking <c>SIDE_INACTIVE(68)</c> handler
    /// (<c>FedSrv.CPP:7379-7425</c>): the mission owner marks an <b>empty</b>
    /// side inactive/active before the mission starts to play a
    /// smaller-than-map-default team count. Starts <c>true</c> — every side
    /// begins active (matching native's <c>rgfActive = true</c> seeding,
    /// <c>fsmission.cpp:59,3503</c>, and every existing test's implicit
    /// assumption). An inactive side (1) is skipped by the mission-wide
    /// AllowEmptyTeams recompute (see <see cref="Mission.AllowEmptyTeams"/>),
    /// (2) is excluded from the alliance groups derived by the server's
    /// <c>UpdateAlliances</c> (native <c>GetSideActive</c>,
    /// <c>fsmission.cpp:5675,5691,5783</c>), and (3) is deliberately <b>not</b>
    /// wired into the P178 win-by-elimination check
    /// (<see cref="Server.CheckForVictoryByInactiveSides"/>), which detects
    /// side-emptiness via connected-peer presence — the P202 scope boundary
    /// keeps this flag for the pre-game toggle only. Follows the same
    /// direct-field-on-Side shape as <see cref="Ready"/>.</summary>
    public bool Active { get; set; } = true;

    /// <summary>
    /// This side's conquest percentage (0–100), the port of the native per-side
    /// <c>m_data.conquest</c> (<c>sideigc.h:525-531</c> Get/SetConquestPercent).
    /// Written only by the mission's conquest recompute
    /// (<see cref="Mission.RecomputeConquestAndTerritory"/>, P191 — the port of
    /// <c>CheckForVictoryByStationKill</c>, <c>fsmission.cpp:3581-3625</c>),
    /// which sets it from the share of <c>Flag</c>-capable
    /// (<see cref="Data.DataStationTypeIGC.SabmFlag"/>) stations the side owns,
    /// and is broadcast to the whole mission as part of GAME_STATE(155) whenever
    /// it (or <see cref="TerritoryCount"/>) changes. Starts 0, matching the
    /// native default; a side untouched by the recompute reads 0.</summary>
    public byte ConquestPercent { get; set; }

    /// <summary>
    /// This side's territory count — how many clusters it owns outright under
    /// the strict unanimous-ownership rule, the port of the native per-side
    /// <c>m_data.territory</c> (<c>sideigc.h:533-541</c> Get/SetTerritoryCount).
    /// Written only by the mission's territory recompute
    /// (<see cref="Mission.RecomputeConquestAndTerritory"/>, P191 — the port of
    /// <c>CheckForVictoryByStationKill</c>'s territory block,
    /// <c>fsmission.cpp:3634-3662</c>): a cluster counts for a side iff every
    /// station in it belongs to that same single side (mixed-ownership and
    /// empty clusters count for nobody). Starts 0.</summary>
    public byte TerritoryCount { get; set; }

    /// <summary>
    /// P201 — how many enemy flags this side has secured, the port of the
    /// native per-side <c>m_data.nFlags</c> (<c>sideigc.h:542-548</c>,
    /// <c>GetFlags</c>/<c>SetFlags</c>, a <c>short</c>). Incremented when a
    /// flag-carrying ship of this side docks at its own side's station with a
    /// real enemy flag (native <c>pside-&gt;SetFlags(pside-&gt;GetFlags()+1)</c>,
    /// FedSrv.CPP:13136) — the genuine, wire-visible source of
    /// <c>GAME_STATE(155)</c>'s <c>nFlags[]</c> (P191's always-zero finding was
    /// incomplete; this is where the counters are populated). The carried flag
    /// is an <b>enemy</b> flag when its value is not the team-lobby artifact
    /// sentinel <see cref="ArtifactSentinelFlag"/> (see
    /// <see cref="Artifacts"/>). Starts 0. Cast to <c>byte</c> when written to
    /// GAME_STATE (native sends these as <c>unsigned char</c>, truncating the
    /// same way).</summary>
    public short Flags { get; set; }

    /// <summary>
    /// P201 — how many neutral "artifacts" this side has secured, the port of
    /// the native per-side <c>m_data.nArtifacts</c> (<c>sideigc.h:551-557</c>,
    /// <c>GetArtifacts</c>/<c>SetArtifacts</c>, a <c>short</c>). Incremented on
    /// the same dock-secure event when the carried "flag" value equals
    /// <see cref="ArtifactSentinelFlag"/> (= <c>SIDE_TEAMLOBBY</c> =
    /// <c>-2</c>, igc.h:514 — a neutral pickup, not a captured enemy flag,
    /// FedSrv.CPP:13127-13132). P201 left the branch structurally present but
    /// unreachable (a real-side flag treasure's TreasureId is a side id >= 0);
    /// <b>P302 makes it reachable</b> — a neutral artifact treasure carries the
    /// sentinel as its TreasureId, and this counter is what the artifact win
    /// condition (<see cref="Mission.RecomputeProsperityAndArtifacts"/>,
    /// <see cref="Mission.ArtifactEnabled"/>) reads to award the game
    /// (<c>fsmission.cpp:3890</c>).</summary>
    public short Artifacts { get; set; }

    /// <summary>
    /// P303 — how many kills this side has scored, the port of the native
    /// per-side kill counter <c>m_data.nKills</c> (<c>sideigc.h:457-464</c>,
    /// <c>GetKills</c>/<c>AddKill</c>, a <c>short</c>). Native credits a side's
    /// kill on the credited-kill path (<c>psideCredit-&gt;AddKill()</c>,
    /// <c>FedSrv.CPP:11868</c> — the non-parent single-credit else branch); this
    /// port increments it on every qualifying cross-side ship kill in the
    /// server's kill-credit block (<c>BroadcastKillShip</c>), the same event that
    /// bumps the killer's <see cref="PlayerScore.Kills"/>. This is what the
    /// <b>kills</b> win condition (<see cref="Mission.RecomputeFlagsAndKills"/>,
    /// <see cref="Mission.KillsEnabled"/>) reads to award the deathmatch game
    /// (<c>CheckForVictoryByKills</c>, <c>fsmission.cpp:3756</c>:
    /// <c>GetKills() &gt;= nGoalTeamKills</c>). Starts 0.</summary>
    public short Kills { get; set; }

    /// <summary>
    /// P303 — how many of this side's ships have died, the port of the native
    /// per-side death counter <c>m_data.nDeaths</c> (<c>sideigc.h:466-473</c>,
    /// <c>GetDeaths</c>/<c>AddDeath</c>, a <c>short</c>). Native credits the
    /// victim's side on each real death; this port increments it alongside the
    /// victim's <see cref="PlayerScore.Deaths"/> in <c>BroadcastKillShip</c>.
    /// Kept for structural faithfulness to native's <c>GetDeaths</c>
    /// (<c>fsmission.cpp:2477-2478</c> uses it as the kills tie-break) and for
    /// completeness; the everyday kills win only reads <see cref="Kills"/>.
    /// Starts 0.</summary>
    public short Deaths { get; set; }

    /// <summary>P201 — the carried-flag value that means "neutral artifact"
    /// rather than "captured enemy flag" — native <c>SIDE_TEAMLOBBY</c>
    /// (<c>igc.h:514</c>). Reached in this port (P302) when a neutral artifact
    /// treasure (TreasureId = this sentinel) is picked up; see
    /// <see cref="Artifacts"/>.</summary>
    public const short ArtifactSentinelFlag = -2;   // SIDE_TEAMLOBBY (igc.h:514)

    /// <summary>
    /// The <see cref="Ship.Id"/> of this side's current team leader, or
    /// <c>null</c> while the side has no leader (P183). The port of the native
    /// per-side <c>m_misdef.rgShipIDLeaders[sideID]</c> slot
    /// (<c>fsmission.cpp</c>): computed only at the two real event sites
    /// <c>AddPlayerToSide</c> (<c>fsmission.cpp:539-661</c> — the first player
    /// onto an empty side becomes its leader) and <c>RemovePlayerFromSide</c>
    /// (<c>fsmission.cpp:848-1099</c> — succession when the leader leaves);
    /// never looked up on demand. Starts <c>null</c>. Follows the same
    /// direct-field-on-Side shape as <see cref="Ready"/> — this port stores
    /// per-side state on the side itself rather than in a side-keyed
    /// dictionary on <see cref="Mission"/>. Written by the Server's
    /// side-switch capability and its disconnect leader-succession.
    /// </summary>
    public short? LeaderShipId { get; set; }

    /// <summary>
    /// P259 — the side's owning <see cref="Mission"/>, wired by
    /// <see cref="Mission.Assemble"/> after construction (the same post-assembly
    /// back-link pattern <see cref="Sector.MissionSides"/> uses for sectors).
    /// Enables the live <see cref="CanSeeCluster"/> query. <c>null</c> for a
    /// side built outside a mission (isolated tests), in which case
    /// <see cref="CanSeeCluster"/> falls back to the cached
    /// <see cref="VisibleClusters"/> set.</summary>
    internal Mission? Mission { get; set; }

    /// <summary>
    /// P259 — this side's <b>cluster visibility range</b>: how far from one of
    /// its live ships/stations the side can bring a neighbouring cluster onto
    /// its radar (distance zero — physical presence — always grants visibility).
    /// Seeded from <see cref="Mission.VisibilityRangeDefault"/> (the original
    /// native <c>c_fcidVisibilityRange</c> constant) at
    /// <see cref="Mission.Assemble"/>; the value is a stated default — see that
    /// constant's honesty note and the P259 writeup. Read by
    /// <see cref="Mission.CanSideSeeCluster"/>.</summary>
    public float VisibilityRange { get; set; } = Mission.VisibilityRangeDefault;

    /// <summary>
    /// P259 — the set of cluster ids this side currently sees, recomputed by
    /// <see cref="Mission.RefreshSideVisibility"/> (driven periodically by
    /// <see cref="Mission.Update"/>). Pure server-side simulation state — no
    /// broadcast, no client surface (P260 and a later starmap overlay consume
    /// it).</summary>
    public IReadOnlySet<short> VisibleClusters { get; internal set; } = new HashSet<short>();

    /// <summary>
    /// P259 — whether this side can currently see cluster
    /// <paramref name="clusterId"/> (see <see cref="Mission.CanSideSeeCluster"/>
    /// for the presence-or-within-range semantics). Queries live sim state
    /// through the side's <see cref="Mission"/> back-link; for a mission-less
    /// side (isolated test) it reads the cached <see cref="VisibleClusters"/>
    /// set instead.</summary>
    public bool CanSeeCluster(short clusterId) =>
        Mission is { } m ? m.CanSideSeeCluster(this, clusterId)
                         : VisibleClusters.Contains(clusterId);

    /// <summary>Construct a side from an already-resolved civilization and its
    /// construction-bucket catalog.</summary>
    public Side(short id, string? name, DataCivilizationIGC civilization, IReadOnlyList<Bucket>? buckets = null)
    {
        Id = id;
        Name = name;
        Civilization = civilization;
        Buckets = buckets ?? Array.Empty<Bucket>();
        // P189: seed the side's development tech tree and global attribute set
        // from the civilization's base data — the port of native
        // `m_data.ttbmDevelopmentTechs = GetCivilization()->GetBaseTechs()`
        // (sideigc.cpp:102) and the side's gas sourced from the civ's
        // `gasBaseAttributes` (civilizationigc.h:73).
        DevelopmentTechs = TechTreeBitMask.FromBytes(civilization.TtbmBaseTechs);
        InitialTechs = DevelopmentTechs;
        GlobalAttributes = GlobalAttributeSet.FromArray(civilization.GasBaseAttributes);
    }

    /// <summary>
    /// Once-per-<see cref="Mission.Update"/> side pass (<c>CsideIGC::Update</c>,
    /// <c>sideigc.h:41</c>): records the time and advances every construction
    /// bucket's invested-build-time accumulator (P119), so a funded bucket
    /// progresses toward completion and the completion seam can fire. Before
    /// P119 this was a counting stub; the bucket advance is the economy-slice
    /// work the native's <c>AdjustBuckets</c>/bucket <c>Update</c> performs.
    /// </summary>
    public void Update(double now)
    {
        UpdateCount++;
        LastUpdate = now;
        foreach (var bucket in Buckets)
            bucket.Update(now);
    }
}
