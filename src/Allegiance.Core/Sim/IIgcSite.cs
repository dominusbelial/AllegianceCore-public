using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>
/// The host seam for a mission — a direct analogue of <c>IIgcSite</c>
/// (<c>src/Igc/igc.h:4757-4924</c>), ported as a C# interface with <b>default
/// implementations</b> so an implementor overrides only what it cares about.
/// This mirrors the C++ "base class with sensible no-op defaults" pattern; it
/// is <em>not</em> an abstract contract.
/// </summary>
/// <remarks>
/// Translation decisions (details in README "The IIgcSite seam"):
/// <list type="bullet">
/// <item><c>Time</c> → <c>double</c> seconds. No existing time type in the C#
/// codebase; the C++ <c>Time</c> is a uint32 millisecond accumulator. double
/// seconds chosen because the tick math is float-seconds and there is no
/// global clock source here.</item>
/// <item>IGC object pointers → the empty marker interfaces in
/// <c>Sim/IgcMarkers.cs</c>.</item>
/// <item>Client/UI-only methods and variadic printf-style text helpers are
/// omitted; see README for the full skip list and rationale.</item>
/// </list>
/// Where a C++ default body needs a member the empty markers cannot provide
/// (<c>KillTreasureEvent</c>, <c>GetCluster</c>, <c>GetRipcordCluster</c>), the
/// body degrades to a safe default and the deviation is called out here and in
/// the README.
/// </remarks>
public interface IIgcSite
{
    // ── clock ──────────────────────────────────────────────────────────────
    // igc.h:4760 — identity on the server (no client/server skew here).
    /// <summary>Identity on the server. C++ <c>igc.h:4760</c>.</summary>
    double ServerTimeFromClientTime(double timeClient) => timeClient;
    /// <summary>Identity on the server. C++ <c>igc.h:4764</c>.</summary>
    double ClientTimeFromServerTime(double timeServer) => timeServer;

    // ── static-core completion / economy callbacks ────────────────────────
    // igc.h:4769-4773, all no-op defaults.
    void DevelopmentCompleted(IBucketIgc b, IDevelopmentIgc d, double now) { }
    void StationTypeCompleted(IBucketIgc b, IStationIgc pstation, IStationTypeIgc st, double now) { }
    void HullTypeCompleted(ISideIgc pside, IHullTypeIgc pht) { }
    void PartTypeCompleted(ISideIgc pside, IPartTypeIgc ppt) { }
    void DroneTypeCompleted(IBucketIgc b, IStationIgc pstation, IDroneTypeIgc dt, double now) { }

    // ── bucket / side / station change notifications ─────────────────────
    // igc.h:4775-4779.
    void BucketChangeEvent(BucketChange bc, IBucketIgc b) { }
    /// <summary>
    /// C#-only (no igc.h member): a real <see cref="Model.Bucket"/> just became
    /// fully funded <b>and</b> fully time-invested (the native gate
    /// bucketigc.h:51). This is the P119 completion-<em>detection</em> seam: it
    /// is raised by <c>Bucket.Complete</c> the instant both thresholds are met,
    /// mirroring how the native bucket's <c>Update</c>/<c>ForceComplete</c>
    /// (bucketigc.h:40-74, 180-262) funnels completion into the site seam. It is
    /// deliberately a no-op default — the task's explicit boundary is to
    /// <b>detect</b> completion without building what a completed bucket
    /// produces (spawning a drone / building a factory / storage pit), which is
    /// the separate follow-up. The concrete <see cref="Model.Bucket"/> is
    /// passed (it implements the <see cref="IBucketIgc"/> marker), same
    /// concrete-model pattern as <see cref="ProjectileCreatedEvent"/> and kin.
    /// </summary>
    void BucketCompleted(IBucketIgc bucket, double now) { }
    void SideBuildingTechChange(ISideIgc s) { }
    void SideDevelopmentTechChange(ISideIgc s) { }
    void SideGlobalAttributeChange(ISideIgc s) { }
    void StationTypeChange(IStationIgc s) { }

    // ── sector-level host callback ────────────────────────────────────────
    // igc.h:4781.
    void ClusterUpdateEvent(IClusterIgc c) { }

    // ── construction ──────────────────────────────────────────────────────
    // igc.h:4783-4787. Note: pbseensides is a caller-owned bool array.
    void BuildStation(
        IAsteroidIgc pasteroid,
        ISideIgc pside,
        IStationTypeIgc pstationtype,
        double now,
        bool[] pbseensides) { }

    /// <summary>P703 host notification after the port's Sector.CompleteBuild performs native
    /// BuildStation. Native removes the rock, exports the owned station and quietly terminates
    /// the builder (FedSrv.CPP:11431-11462,12025; buildingeffectigc.h:315-343).</summary>
    void StationBuiltEvent(IAsteroidIgc asteroid, IShipIgc builder, IStationIgc station) { }

    // igc.h:4789-4790 — the default creates a fresh, unattached shim.
    /// <summary>Creates a per-object shim. C++ default <c>new ThingSite</c> (<c>igc.h:4789</c>).</summary>
    ThingSite CreateThingSite(IModelIgc pModel) => new ThingSite();
    /// <summary>Creates a per-sector shim. C++ default <c>new ClusterSite</c> (<c>igc.h:4790</c>).</summary>
    ClusterSite CreateClusterSite(IClusterIgc pCluster) => new ClusterSite();

    // ── chat ───────────────────────────────────────────────────────────────
    // igc.h:4792-4800. ReceiveChat is server-side host handling of incoming
    // chat; no-op default. (SendChatf, the variadic printf helper, is skipped —
    // see README.)
    void ReceiveChat(
        IShipIgc pshipSender,
        ChatTarget ctRecipient,
        short oidRecipient,
        short voiceOver,
        string szText,
        short cid,
        IgcObjectType otTarget,
        short oidTarget,
        IModelIgc? pmodelTarget = null) { }

    // igc.h:4801-4810. SendChat routes chat to clients; no-op default.
    void SendChat(
        IShipIgc pshipSender,
        ChatTarget chatTarget,
        short oidRecipient,
        short soVoiceOver,
        string szText,
        short cid = -1,            // c_cidNone
        IgcObjectType otTarget = IgcObjectType.Ship,  // NA maps to Ship (0) — see README
        short oidTarget = -1,      // NA
        IModelIgc? pmodelTarget = null,
        bool bObjectModel = false) { }

    // ── economy ───────────────────────────────────────────────────────────
    // igc.h:4828. Money is int, HullID is short (both igc.h:477,483,840).
    void GetMoneyRequest(IShipIgc pshipSender, int amount, short hidFor) { }

    // ── termination / destruction ─────────────────────────────────────────
    // igc.h:4836-4846.
    void TerminateModelEvent(IModelIgc model) { }
    void TerminateMissionEvent(IMissionIgc pMission) { }
    void KillAsteroidEvent(IAsteroidIgc pasteroid, bool explodeF) { }
    // Imago #120 #121 — igc.h:4839 (asteroid-id + sector-id overload).
    void KillAsteroidEvent(short roid, short soid, ISideIgc side) { }
    void DrainAsteroidEvent(IAsteroidIgc pasteroid) { }
    // Xynth #132 — igc.h:4841.
    void MineAsteroidEvent(IAsteroidIgc pasteroid, float newOre) { }
    void KillProbeEvent(IProbeIgc pprobe) { }
    void KillMissileEvent(IMissileIgc pmissile, Vector3 position) { }
    void KillBuildingEffectEvent(IBuildingEffectIgc pbe) { }
    void KillMineEvent(IMineIgc pmine) { }
    /// <summary>
    /// C++ default <c>ptreasure-&gt;Terminate()</c> (<c>igc.h:4846</c>) is
    /// <b>not</b> reproduced: it needs a <c>Terminate()</c> member on
    /// <c>ItreasureIGC</c>, which the empty marker <see cref="ITreasureIgc"/>
    /// forbids. Default is a no-op until the object model supplies
    /// <c>Terminate</c>. See README.
    /// </summary>
    void KillTreasureEvent(ITreasureIgc ptreasure) { }

    /// <summary>
    /// P586 — a live treasure expired / was destroyed (<c>CtreasureIGC::Update</c> →
    /// <c>KillTreasureEvent</c>, <c>treasureIGC.cpp:215-218</c>). Concrete-typed
    /// (<see cref="Treasure"/>), the same modern pattern as
    /// <see cref="MineDestroyedEvent"/> / <see cref="MissileDestroyedEvent"/> /
    /// <see cref="ProbeDestroyedEvent"/> (the marker-based
    /// <see cref="KillTreasureEvent(ITreasureIgc)"/> above is the legacy native
    /// igc.h surface and stays unused). Raised by <c>Treasure.OnUpdate</c> on
    /// expiry (alongside <c>sector.Remove(this)</c>) so a host can broadcast the
    /// wire <c>FM_S_DESTROY_TREASURE(30)</c>. No-op default; a host that has not
    /// installed a handler behaves exactly as before.
    /// </summary>
    void TreasureDestroyedEvent(Treasure t) { }

    /// <summary>New ambient treasure / artifact (Igc/missionigc.cpp:4207-4213).</summary>
    void TreasureGeneratedEvent(Treasure t) { }

    // ── text (console messages to clients) ────────────────────────────────
    // igc.h:4847. (PostText / PostNotificationText variadic wrappers skipped.)
    void PostPlainText(bool bCritical, string pszText) { }

    /// <summary>Mounted sound and owner ready text: weaponIGC.h:44-46,
    /// cloakigc.cpp:75-76, shieldIGC.cpp:82-83, afterburnerIGC.cpp:119-120.
    /// The historical method name also serves these utility parts.</summary>
    void WeaponReady(Ship ship, string partName) { }

    /// <summary>Port host adapter: queued hosts drain completed teleports after
    /// Mission.Update. Other sites use the native ContinueRipcord/UseRipcord
    /// callbacks directly, including false-at-zero retries (shipIGC.cpp:278-292).</summary>
    bool QueueRipcordUse => false;

    // ── player / ship events ──────────────────────────────────────────────
    // igc.h:4850-4854.
    void EjectPlayer(IModelIgc pmodelCredit) { }
    void TargetKilled(IModelIgc pmodel) { }
    void ShipWarped(IShipIgc pship, short sidOld, short sidNew) { }
    void DamageShipEvent(
        double now, IShipIgc ship, IModelIgc? launcher,
        byte type, float amount, float leakage,
        Vector3 p1, Vector3 p2) { }
    void KillShipEvent(
        double now, IShipIgc ship, IModelIgc? launcher,
        float amount, Vector3 p1, Vector3 p2) { }

    /// <summary>
    /// C#-only (no igc.h member): a real <see cref="Projectile"/> model was just
    /// created in a sector by <c>Weapon.FireWeapon</c> (after
    /// <c>sector.Add(projectile)</c>). This is the host seam P88 uses to
    /// broadcast the <c>FM_CS_PROJECTILE_INSTANCE(12)</c> message — the real
    /// game's wire message for a fresh projectile (<c>Messages.h:131-135</c>).
    /// Taking the concrete <see cref="Projectile"/> (there is no empty marker
    /// for a projectile — the markers only exist where igc.h's seam params
    /// demand one), the same pattern as the C#-only
    /// <see cref="ReportNotable"/>/<see cref="SideUpdated"/> hooks: a no-op
    /// default so a host that has not installed a handler behaves as before.
    /// </summary>
    void ProjectileCreatedEvent(Projectile p) { }

    /// <summary>
    /// C#-only (no igc.h member): a real <see cref="Missile"/> model was just
    /// created in a sector by <c>Ship.FireMissile</c> (after
    /// <c>sector.Add(missile)</c>). This is the host seam P90 uses to broadcast
    /// the <c>FM_CS_FIRE_MISSILE(70)</c> message — the real game's wire launch
    /// notice (<c>Messages.h:547-557</c>). The concrete <see cref="Missile"/> is
    /// passed (there is no empty marker for a missile), the same pattern as
    /// <see cref="ProjectileCreatedEvent"/>: a no-op default so a host that has
    /// not installed a handler behaves as before. Note this deliberately does
    /// <em>not</em> reuse the C++-seam <c>FireMissile(IShipIgc, IMagazineIgc, ...)</c>
    /// (declared above, igc.h:4868-4871): that signature names a magazine the
    /// caller supplies — a mountable-launcher concept that does not exist in
    /// this port (see <c>DataPartTypeIGC.EquipmentType</c> — there is no
    /// <c>Launcher</c> case) — so the creation notice carries the concrete
    /// missile whose own <c>MissileType</c>/<c>Position</c>/<c>Velocity</c>/
    /// <c>Forward</c> supply everything the wire message needs.
    /// </summary>
    void MissileCreatedEvent(Missile m) { }

    /// <summary>
    /// C#-only (no igc.h member): a real <see cref="Missile"/> just expired and
    /// removed itself from its sector. Raised from the missile's own expiry path
    /// in <see cref="Missile.OnUpdate"/>. This is the host seam P90 uses to
    /// broadcast a real <c>FM_S_MISSILE_DESTROYED(83)</c> — the "this missile
    /// was destroyed here" notice (<c>Messages.h:597-601</c>). Mirrors how a real
    /// ship death raises the igc.h <c>KillMissileEvent</c>-adjacent
    /// <see cref="KillShipEvent"/> (Ship.cs:494); the marker-based igc.h
    /// <c>KillMissileEvent(IMissileIgc, Vector3)</c> declared above cannot be
    /// fleshed out (the empty <see cref="IMissileIgc"/> marker exposes no id /
    /// position), so this concrete-Missile event is the workable C# analogue.
    /// No-op default; a host that has not installed a handler behaves as before.
    /// </summary>
    void MissileDestroyedEvent(Missile m) { }

    /// <summary>
    /// C#-only (no igc.h member): a real <see cref="Projectile"/> just struck a
    /// damagable, visible target and dealt hull-or-worse damage without a blast
    /// and without the native <c>c_dmgidNoDebris</c> suppression flag — the exact
    /// gate at which native fires <c>pModel-&gt;GetThingSite()-&gt;AddHullHit(pos,
    /// pos.Normalize())</c> (<c>src/Igc/projectileIGC.cpp:220-226</c>). This is the
    /// host seam P364 uses to broadcast the <c>FM_S_PROJECTILE_HULL_HIT(213)</c>
    /// <see cref="Wire.ProjectileHullHit"/> — the "a weapon shot connected here"
    /// visual notice — so a pure-renderer client (which never simulates combat)
    /// can spawn the impact spark at the real impact point/normal. Raised from
    /// <c>Sector.ResolveCombatCollisions</c> on the sim thread, mirroring how the
    /// launcher-routed destruction notices (<see cref="KillShipEvent"/>, &amp;c.)
    /// are raised there. The concrete <see cref="Projectile"/> is passed (the
    /// server host derives the shooter/side for scoping the broadcast, exactly as
    /// <see cref="ProjectileCreatedEvent"/>'s projectile), together with the
    /// impact position/normal and the struck target's radius/id/cluster — the
    /// port of the native call's arguments. No-op default; a host that has not
    /// installed a handler behaves as before.
    /// </summary>
    void ProjectileHitEvent(Projectile p, Vector3 worldImpact, Vector3 worldNormal,
        float targetRadius, short targetId, short clusterId, bool absorbedByShield) { }

    /// <summary>
    /// C#-only (no igc.h member): a SHIP just took damage from a ship-launched
    /// round — P623's seam for the client's TK_TargetHostile attribution (the
    /// port of native's same-process <c>DamageBucket</c> population, which a
    /// pure-renderer client cannot do for itself). Raised from
    /// <c>Sector.ResolveCombatCollisions</c> on the sim thread immediately after
    /// <c>Ship.ReceiveDamage</c> returns real damage, with the attacking ship's
    /// id, the struck ship's id, and the damage amount dealt (the same
    /// hull/shield amount that keys native's bucket, ranked by severity). The
    /// server host unicasts this to the VICTIM's own peer only. No-op default.
    /// </summary>
    void VictimDamagedEvent(short attackerShipId, short victimShipId, float damage) { }

    /// <summary>
    /// C#-only (no igc.h member): a real <see cref="Mine"/> model was just
    /// created in a sector by <see cref="Ship.DeployMine"/> (after
    /// <c>sector.Add(mine)</c>). This is the host seam P94 uses to broadcast the
    /// <c>FM_S_EXPORT(26)</c>/<c>OT_mine</c> message — the real game's wire
    /// notice for a fresh mine, sent through the <b>same generic</b>
    /// <c>CreateObject(..., OT_mine, &amp;obj, sizeof(obj))</c> path every other
    /// exported object uses (<c>FedSrv.CPP:10072</c>), and exported by the
    /// native client via <c>CmineIGC::Export</c> (<c>src/Igc/mineigc.cpp:158-185</c>).
    /// The concrete <see cref="Mine"/> is passed (there is no empty marker for a
    /// mine), the same pattern as <see cref="ProjectileCreatedEvent"/> /
    /// <see cref="MissileCreatedEvent"/>. The mine's own <c>MineType</c>,
    /// <c>Position</c>, <c>Id</c> and <c>Launcher</c> supply everything the wire
    /// message needs. No-op default so a host that has not installed a handler
    /// behaves as before.
    /// </summary>
    void MineCreatedEvent(Mine m) { }

    /// <summary>
    /// C#-only (no igc.h member): a real <see cref="Mine"/> just expired and
    /// removed itself from its sector. Raised from the mine's own expiry path in
    /// <see cref="Mine.OnUpdate"/>. This is the host seam P94 uses to broadcast a
    /// real <c>FM_S_MINE_DESTROYED(84)</c> — the \"this mine blew up\" notice
    /// (<c>Messages.h:603-606</c>). The marker-based igc.h <c>KillMineEvent</c>
    /// declared above (<c>igc.h:4843</c>) cannot be fleshed out (the empty
    /// <see cref="IMineIgc"/> marker exposes no id / position), so this
    /// concrete-Mine event is the workable C# analogue, exactly as
    /// <see cref="MissileDestroyedEvent"/> is for missiles. No-op default; a host
    /// that has not installed a handler behaves as before.
    /// </summary>
    void MineDestroyedEvent(Mine m) { }

    /// <summary>
    /// P574 — a live ship took a real minefield hit this tick and the 0.5 s
    /// per-ship pop throttle granted the accompanying explosion. Raised from
    /// <see cref="Sector.ResolveMineDetonations"/>'s worst-hit application pass,
    /// the port of the native <c>ApplyMineDamage</c> pair
    /// (<c>shipIGC.h:2021-2024</c>):
    /// <code>
    ///   if (m_maxMineTime > m_timeLastMineExplosion + 0.5f) {
    ///       m_timeLastMineExplosion = m_maxMineTime;
    ///       GetCluster()->GetClusterSite()->AddExplosion(m_maxMineP2, 1.5f, c_etMine);
    ///   }
    /// </code>
    /// The host uses this to broadcast the per-hit <c>c_etMine</c> pop at the
    /// native P2 placement (ahead of the ship along its path + the 10-unit
    /// random offset) — <b>not</b> once at the field centre. Carries the
    /// <see cref="Mine"/> (for its sector/audience and current strength) and the
    /// native <c>m_maxMineP1</c>/<c>m_maxMineP2</c> (both stored by
    /// <c>AddMineDamage</c>, <c>shipIGC.h:2010-2011</c>; the pop uses P2). No-op
    /// default; a host that has not installed a handler behaves as before.
    /// </summary>
    void MineHitEvent(Mine m, Vector3 p1, Vector3 p2) { }

    /// <summary>
    /// C#-only (no igc.h member): a real <see cref="Probe"/> model was just
    /// created in a sector by <see cref="Ship.DeployProbe"/> (after
    /// <c>sector.Add(probe)</c>). This is the host seam P99 uses to broadcast
    /// the <c>FM_S_EXPORT(26)</c>/<c>OT_probe</c> message — the real game's
    /// wire notice for a fresh probe, sent through the <b>same generic</b>
    /// <c>CreateObject(..., OT_probe, &amp;obj, sizeof(obj))</c> path every other
    /// exported object uses and exported by the native client via
    /// <c>CprobeIGC::Export</c> (<c>src/Igc/probeigc.cpp:535-568</c>). The
    /// concrete <see cref="Probe"/> is passed (there is no empty marker for a
    /// probe), the same pattern as <see cref="MineCreatedEvent"/>. The probe's
    /// own <c>ProbeType</c>, <c>Position</c>, <c>Id</c>, <c>SideId</c> and
    /// <c>Launcher</c> supply everything the wire message needs. No-op default
    /// so a host that has not installed a handler behaves as before.
    /// </summary>
    void ProbeCreatedEvent(Probe p) { }

    /// <summary>
    /// C#-only (no igc.h member): a real <see cref="Probe"/> just expired and
    /// removed itself from its sector. Raised from the probe's own expiry path
    /// in <see cref="Probe.OnUpdate"/>, mirroring
    /// <see cref="MineDestroyedEvent"/> exactly. This is the host seam P99
    /// uses to broadcast a real <c>FM_S_PROBE_DESTROYED(85)</c> — the
    /// &quot;this probe was destroyed here&quot; notice
    /// (<c>Messages.h:608-611</c>). Its intent is the native igc.h
    /// <c>KillProbeEvent</c> (<c>igc.h:4836</c>), the single real event the
    /// probe's expiry invokes (<c>probeigc.cpp:335-336</c>); it keeps this
    /// project's own <c>XxxDestroyedEvent</c> naming convention for
    /// consistency with <see cref="MineDestroyedEvent"/> /
    /// <see cref="MissileDestroyedEvent"/>. The concrete <see cref="Probe"/> is
    /// passed (the marker-based igc.h <c>KillProbeEvent</c> cannot be fleshed
    /// out — the empty <see cref="IProbeIgc"/> marker exposes no id/sector),
    /// the same pattern as <see cref="MineDestroyedEvent"/>. No-op default; a
    /// host that has not installed a handler behaves as before.
    /// </summary>
    void ProbeDestroyedEvent(Probe p) { }

    // ── station damage / capture / docking ────────────────────────────────
    // igc.h:4855-4860.
    void DamageStationEvent(IStationIgc station, IModelIgc launcher, byte type, float amount) { }
    void KillStationEvent(IStationIgc station, IModelIgc launcher, float amount) { }
    /// <summary>Permitted by default. C++ <c>igc.h:4857</c>.</summary>
    bool DockWithStationEvent(IShipIgc ship, IStationIgc station) => true;
    void CaptureStationEvent(IShipIgc ship, IStationIgc station) { }

    /// <summary>
    /// C#-only (no igc.h member): a station capture/kill just ran the P191
    /// conquest/territory victory recompute
    /// (<see cref="Allegiance.Core.Model.Mission.RecomputeConquestAndTerritory"/>,
    /// the port of <c>CFSMission::CheckForVictoryByStationKill</c>,
    /// <c>fsmission.cpp:3568-3710</c>). <paramref name="mission"/> is the mission
    /// that was recomputed (for scoping the whole-mission GAME_STATE(155)
    /// broadcast and any P178 GameOver to the right mission on a multi-mission
    /// server); <paramref name="changed"/> is the native <c>bChange</c> gate —
    /// true iff at least one side's <c>ConquestPercent</c>/<c>TerritoryCount</c>
    /// actually changed, which decides whether the host broadcasts GAME_STATE at
    /// all; <paramref name="winner"/> is the side that crossed its win threshold
    /// (a non-null value means the host should declare the mission over via the
    /// P178 GameOver path), or <c>null</c>. No-op default so a host that has not
    /// installed a handler behaves as before — matching the seam style of
    /// <see cref="ProjectileCreatedEvent"/> and kin.</summary>
    void VictoryByStationsComputed(Allegiance.Core.Model.Mission mission, bool changed, Allegiance.Core.Model.Side? winner) { }

    /// <summary>
    /// P455 — a side just lost one of its restart-capable stations, to a kill
    /// (<c>Sector.ResolveCombatCollisions</c>) or a capture
    /// (<c>Sector.ResolveDockCollisions</c>). <paramref name="sideId"/> is the
    /// side that lost the station — its owner BEFORE the kill/re-side (native's
    /// <c>psideOld</c>, fsmission.cpp:3699). The host decides whether that was
    /// the side's LAST restart station and, if so, eliminates the side
    /// (<c>DeactivateSide</c>) — native's station-elimination win,
    /// fsmission.cpp:3698-3714 ("Elimiation of all restart stations of all
    /// other sides is always a win"). C#-only (no igc.h member). No-op default,
    /// matching the seam style of <see cref="VictoryByStationsComputed"/>.</summary>
    void SideLostRestartStation(Allegiance.Core.Model.Mission mission, short sideId) { }

    /// <summary>
    /// C#-only (no igc.h member): the P302 prosperity/artifact win conditions
    /// just recomputed (<see cref="Allegiance.Core.Model.Mission.RecomputeProsperityAndArtifacts"/>,
    /// the port of the <c>c_didTeamMoney</c> economic-victory trigger,
    /// <c>FedSrv.CPP:11274-11279</c>, and the artifact leg of
    /// <c>CheckForVictoryByFlags</c>, <c>fsmission.cpp:3888-3894</c>).
    /// <paramref name="mission"/> scopes the whole-mission broadcast and any
    /// P178 GameOver to the right mission on a multi-mission server;
    /// <paramref name="changed"/> is whether any win-relevant state changed
    /// (mirrors P191's <c>bChange</c> gate); <paramref name="winner"/> is the
    /// side that crossed its prosperity/artifact threshold (non-null ⇒ the
    /// host should declare the mission over via the P178 GameOver path), or
    /// <c>null</c>. No-op default, matching the seam style of
    /// <see cref="VictoryByStationsComputed"/>.</summary>
    void ProsperityArtifactComputed(Allegiance.Core.Model.Mission mission, bool changed, Allegiance.Core.Model.Side? winner) { }

    /// <summary>
    /// C#-only (no igc.h member): the P303 flags/kills win conditions just
    /// recomputed (<see cref="Allegiance.Core.Model.Mission.RecomputeFlagsAndKills"/>,
    /// the port of the flag leg of <c>CheckForVictoryByFlags</c>,
    /// <c>fsmission.cpp:3883-3887</c>, and <c>CheckForVictoryByKills</c>,
    /// <c>fsmission.cpp:3749-3760</c>). <paramref name="mission"/> scopes the
    /// whole-mission broadcast and any P178 GameOver to the right mission on a
    /// multi-mission server; <paramref name="changed"/> is whether any
    /// win-relevant state changed (mirrors P191's <c>bChange</c> gate);
    /// <paramref name="winner"/> is the side that crossed its flags/kills
    /// threshold (non-null ⇒ the host should declare the mission over via the
    /// P178 GameOver path), or <c>null</c>. No-op default, matching the seam
    /// style of <see cref="VictoryByStationsComputed"/>.</summary>
    void FlagsKillsComputed(Allegiance.Core.Model.Mission mission, bool changed, Allegiance.Core.Model.Side? winner) { }

    /// <summary>Permitted by default. C++ <c>igc.h:4859</c>.</summary>
    bool LandOnCarrierEvent(IShipIgc shipCarrier, IShipIgc ship, float tCollision) => true;
    /// <summary>Permitted by default. C++ <c>igc.h:4860</c>.</summary>
    bool RescueShipEvent(IShipIgc shipRescued, IShipIgc shipRescuer) => true;

    // ── warps / treasures ─────────────────────────────────────────────────
    // igc.h:4861-4866.
    void WarpBombEvent(IWarpIgc pwap, IMissileIgc pmissile) { }
    void HitWarpEvent(IShipIgc ship, IWarpIgc warp) { }
    /// <summary>Accepted by default. C++ <c>igc.h:4863</c>.</summary>
    bool HitTreasureEvent(double now, IShipIgc ship, ITreasureIgc treasure) => true;
    void PaydayEvent(ISideIgc pside, float money) { }

    // ── ripcord ───────────────────────────────────────────────────────────
    // igc.h:4865-4867.
    void RequestRipcord(IShipIgc ship, IClusterIgc pcluster) { }
    /// <summary>Allowed by default. C++ <c>igc.h:4866</c>.</summary>
    bool ContinueRipcord(IShipIgc ship, IModelIgc pmodel) => true;
    /// <summary>Denied by default. C++ <c>igc.h:4867</c>.</summary>
    bool UseRipcord(IShipIgc ship, IModelIgc pmodel) => false;

    // ── combat firing ─────────────────────────────────────────────────────
    // igc.h:4868-4871.
    void FireMissile(IShipIgc pship, IMagazineIgc pmagazine, double timeFired, IModelIgc pTarget, float lockFraction) { }
    void FireExpendable(IShipIgc pship, IDispenserIgc pdispenser, double timeFired) { }
    // GetDroneFromShip (igc.h:4872) skipped — returns an internal Drone*, not
    // an IGC interface; see README.

    // ── side lifecycle ────────────────────────────────────────────────────
    // igc.h:4873-4874.
    void CreateSideEvent(ISideIgc pIsideIGC) { }
    void DestroySideEvent(ISideIgc pIsideIGC) { }

    // ── instrumentation (C# only — NOT part of the C++ seam) ──────────────
    // The mission tick calls side->Update(now) once per Update; the C++
    // side Update (sideigc.h:41) generates NO host callback, so a recording
    // site sees no side activity and cannot prove "all side updates precede
    // the first cluster chunk". This default method is the observation hook the
    // tick test uses to witness the side pass through the same seam — it is
    // deliberately a no-op default, so it changes nothing for a real host, and
    // is labelled so nobody mistakes it for an igc.h member. The cluster pass
    // already has a faithful member (ClusterUpdateEvent, clusterIGC.cpp:357),
    // so only the side pass needs this.
    void SideUpdated(ISideIgc side, double now) { }
    /// <summary>
    /// Relay a pre-formatted "notable moment" line (dock, mine-start,
    /// mine-sell, undock, payday, warp crossing) from the sim layer up to the
    /// host, so the host can forward it to observers (e.g. the spectator
    /// event feed) WITHOUT reformatting. Deliberately takes the ready-made
    /// text string rather than the structured event: the human-readable line
    /// already composed at the call site (the same string handed to
    /// <see cref="Console.WriteLine(string?)"/>) doubles as the feed text, so
    /// the feed can never drift from what the console shows. No-op default —
    /// a headless host that has not installed a handler behaves as before.
    /// </summary>
    void ReportNotable(string text) { }

    // ── cluster-change routing ────────────────────────────────────────────
    // igc.h:4876-4881.
    void ChangeStation(IShipIgc pship, IStationIgc pstationOld, IStationIgc pstationNew) { }
    void ChangeCluster(IShipIgc pship, IClusterIgc pclusterOld, IClusterIgc pclusterNew) { }
    void CommandChangedEvent(short i, IShipIgc pship, IModelIgc ptarget, short cid) { }
    /// <summary>C++ default <c>false</c> (<c>igc.h:4883</c>).</summary>
    bool HandlePickDefaultOrder(IShipIgc pship) => false;

    // ── asset preload / teleport probes ───────────────────────────────────
    // igc.h:4885-4890.
    void Preload(string? pszModelName, string? pszFileName) { }
    void ActivateTeleportProbe(IProbeIgc pprobe) { }
    void DestroyTeleportProbe(IProbeIgc pprobe) { }

    // ── loadout ───────────────────────────────────────────────────────────
    // igc.h:4892.
    void LoadoutChangeEvent(IShipIgc pship, IPartIgc ppart, LoadoutChange lc) { }

    // ── game-state / building-effect / expendables ────────────────────────
    // igc.h:4899, 4901-4907.
    void ChangeGameState() { }
    void CreateBuildingEffect(double now, IAsteroidIgc pasteroid, IShipIgc pshipBuilder) { }
    void LayExpendable(double now, IExpendableTypeIgc pet, IShipIgc pshipLayer) { }

    // ── cluster lookup ────────────────────────────────────────────────────
    // igc.h:4909-4913. The C++ default uses pship->CanSee(pmodel) /
    // pmodel->GetCluster(); both are members the empty markers cannot express,
    // so the default degrades to null and is documented as a deviation.
    IClusterIgc? GetCluster(IShipIgc pship, IModelIgc pmodel) => null;

    // igc.h:4915-4921. The C++ default consults the ship's base-hull
    // capabilities (GetBaseHullType/HasCapability) — impossible on the empty
    // markers — so it degrades to null. Deviation, see README.
    IClusterIgc? GetRipcordCluster(IShipIgc pship, short habm = 0x0040 /* c_habmIsRipcordTarget */) => null;

    // ── reload ────────────────────────────────────────────────────────────
    // igc.h:4923.
    /// <summary>C++ default <c>false</c> (<c>igc.h:4923</c>).</summary>
    bool Reload(IShipIgc pship, ILauncherIgc plauncher, EquipmentType type) => false;

    // ── auto-reload on empty (P361) ───────────────────────────────────────
    // Raised by Ship.TryAutoReloadAmmo (the port of the auto-reload block
    // inside native CshipIGC::SetAmmo/SetFuel, shipIGC.h:597-621,627-648) after
    // it arms the debounce and applies the P192 cargo→pool transfer. The
    // receiver (ServerHost → Server) broadcasts the RELOAD(129) message
    // sector-wide on success so peers observe the transfer, exactly like the
    // manual FM_CS_RELOAD handler does (Server.HandleReload). <c>success</c> is
    // true when a spare pack was consumed ("Reloading ammo...") and false when
    // the cargo hold held no matching pack ("Ammo depleted."). Empty entries on
    // the success leg are impossible (success implies >= 1 pack applied). No-op
    // default, matching the seam convention.
    void AutoReloadTriggered(IShipIgc pship,
                             IReadOnlyList<global::Allegiance.Core.Wire.ReloadData> entries,
                             bool success) { }

    /// <summary>
    /// C#-only (no igc.h member): a real ship↔ship, ship↔asteroid or
    /// ship↔station collision was just resolved by one of <c>Sector</c>'s
    /// per-tick resolvers (<c>ResolveShipShipCollisions</c>,
    /// <c>ResolveShipAsteroidCollisions</c>, <c>ResolveShipStationCollisions</c>
    /// — P354/P254/P466). This is the host seam P463c uses to broadcast the
    /// <c>COLLISION_EVENT(216)</c> <see cref="Wire.CollisionEvent"/> — the
    /// \"two bodies collided here\" notice — so a pure-renderer client (which
    /// never simulates physics) can play the positional collision sound and feed
    /// its own ship's impact into P449b's camera jiggle. Raised on the sim
    /// thread inside the sector tick that resolved the collision, mirroring how
    /// <see cref="ProjectileHitEvent"/> is raised. <paramref name="party1"/> is
    /// the ship (always); <paramref name="party2"/> is the other ship, or
    /// <see langword="null"/> for an asteroid/station party (native shakes only
    /// the ship and plays the sound only for the two ships in a ship-ship hit).
    /// <paramref name="jiggle1"/>/<paramref name="jiggle2"/> are the native
    /// <c>effectJiggle</c> intensities computed with
    /// <c>c_impactJiggleCoefficient = 1/20</c> (shipIGC.cpp:624,836,1004,1191,1195),
    /// per party (party2's is 0 for a rock/station). No-op default; a host that
    /// has not installed a handler behaves as before.
    /// </summary>
    void CollisionEvent(Ship party1, Ship? party2, short clusterId, Vector3 impact,
        float jiggle1, float jiggle2) { }
}
