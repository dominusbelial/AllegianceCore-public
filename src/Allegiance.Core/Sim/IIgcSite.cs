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

    // ── text (console messages to clients) ────────────────────────────────
    // igc.h:4847. (PostText / PostNotificationText variadic wrappers skipped.)
    void PostPlainText(bool bCritical, string pszText) { }

    // ── player / ship events ──────────────────────────────────────────────
    // igc.h:4850-4854.
    void EjectPlayer(IModelIgc pmodelCredit) { }
    void TargetKilled(IModelIgc pmodel) { }
    void ShipWarped(IShipIgc pship, short sidOld, short sidNew) { }
    void DamageShipEvent(
        double now, IShipIgc ship, IModelIgc launcher,
        byte type, float amount, float leakage,
        Vector3 p1, Vector3 p2) { }
    void KillShipEvent(
        double now, IShipIgc ship, IModelIgc launcher,
        float amount, Vector3 p1, Vector3 p2) { }

    // ── station damage / capture / docking ────────────────────────────────
    // igc.h:4855-4860.
    void DamageStationEvent(IStationIgc station, IModelIgc launcher, byte type, float amount) { }
    void KillStationEvent(IStationIgc station, IModelIgc launcher, float amount) { }
    /// <summary>Permitted by default. C++ <c>igc.h:4857</c>.</summary>
    bool DockWithStationEvent(IShipIgc ship, IStationIgc station) => true;
    void CaptureStationEvent(IShipIgc ship, IStationIgc station) { }
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
}
