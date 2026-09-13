using System.Text;

using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>
/// An <see cref="IIgcSite"/> that records every invocation (method name plus
/// salient arguments) into an ordered list, delegating each call to the
/// interface's default implementation so recorded behavior still matches what a
/// plain site would do. This is the observation harness the tick test will use.
/// </summary>
public sealed class RecordingIgcSite : IIgcSite
{
    private readonly List<string> _calls = new();
    private readonly StringBuilder _buf = new();

    // The default implementations are reached by delegating to an inner
    // NullIgcSite. Casting `this` to IIgcSite would instead re-dispatch to this
    // class's own overrides (a class member shadows an interface default in
    // dispatch), recursing forever; an inner instance that overrides nothing
    // is where the interface defaults actually live.
    private readonly IIgcSite _default = new NullIgcSite();

    /// <summary>Every recorded call, in arrival order. Format <c>Name(args)</c>.</summary>
    public IReadOnlyList<string> Calls => _calls;

    /// <summary>True when at least one call has been recorded.</summary>
    public bool Any => _calls.Count > 0;

    /// <summary>Clears the recorded calls.</summary>
    public void Reset() => _calls.Clear();

    private void Log(string name, params object?[] args)
    {
        _buf.Clear();
        _buf.Append(name).Append('(');
        for (var i = 0; i < args.Length; i++)
        {
            if (i > 0) _buf.Append(", ");
            if (args[i] is null) _buf.Append("null");
            else if (args[i] is Array arr) _buf.Append('[').Append(arr.Length).Append(']');
            else if (args[i] is float f) _buf.Append(f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            else
            {
                var t = args[i]!.GetType();
                if (t.IsEnum || t.IsPrimitive || args[i] is string)
                    _buf.Append(args[i]);
                else
                    _buf.Append(t.Name);  // a marker instance -> its type name
            }
        }
        _buf.Append(')');
        _calls.Add(_buf.ToString());
    }

    private T Wrap<T>(string name, Func<T> body, params object?[] args)
    {
        Log(name, args);
        return body();
    }

    private void Wrap(string name, Action body, params object?[] args)
    {
        Log(name, args);
        body();
    }

    // ── clock ─────────────────────────────────────────────────────────────
    public double ServerTimeFromClientTime(double timeClient) =>
        Wrap(nameof(ServerTimeFromClientTime),
            () => _default.ServerTimeFromClientTime(timeClient), timeClient);
    public double ClientTimeFromServerTime(double timeServer) =>
        Wrap(nameof(ClientTimeFromServerTime),
            () => _default.ClientTimeFromServerTime(timeServer), timeServer);

    // ── static-core completion / economy ──────────────────────────────────
    public void DevelopmentCompleted(IBucketIgc b, IDevelopmentIgc d, double now) =>
        Wrap(nameof(DevelopmentCompleted), () => { }, b, d, now);
    public void StationTypeCompleted(IBucketIgc b, IStationIgc pstation, IStationTypeIgc st, double now) =>
        Wrap(nameof(StationTypeCompleted), () => { }, b, pstation, st, now);
    public void HullTypeCompleted(ISideIgc pside, IHullTypeIgc pht) =>
        Wrap(nameof(HullTypeCompleted), () => { }, pside, pht);
    public void PartTypeCompleted(ISideIgc pside, IPartTypeIgc ppt) =>
        Wrap(nameof(PartTypeCompleted), () => { }, pside, ppt);
    public void DroneTypeCompleted(IBucketIgc b, IStationIgc pstation, IDroneTypeIgc dt, double now) =>
        Wrap(nameof(DroneTypeCompleted), () => { }, b, pstation, dt, now);

    // ── bucket / side / station change notifications ─────────────────────
    public void BucketChangeEvent(BucketChange bc, IBucketIgc b) =>
        Wrap(nameof(BucketChangeEvent), () => { }, bc, b);
    public void BucketCompleted(IBucketIgc bucket, double now) =>
        Wrap(nameof(BucketCompleted), () => { }, bucket, now);
    public void SideBuildingTechChange(ISideIgc s) =>
        Wrap(nameof(SideBuildingTechChange), () => { }, s);
    public void SideDevelopmentTechChange(ISideIgc s) =>
        Wrap(nameof(SideDevelopmentTechChange), () => { }, s);
    public void SideGlobalAttributeChange(ISideIgc s) =>
        Wrap(nameof(SideGlobalAttributeChange), () => { }, s);
    public void StationTypeChange(IStationIgc s) =>
        Wrap(nameof(StationTypeChange), () => { }, s);

    // ── sector-level host callback ───────────────────────────────────────
    public void ClusterUpdateEvent(IClusterIgc c) =>
        Wrap(nameof(ClusterUpdateEvent), () => { }, c);

    // ── construction ──────────────────────────────────────────────────────
    public void BuildStation(
        IAsteroidIgc pasteroid, ISideIgc pside, IStationTypeIgc pstationtype,
        double now, bool[] pbseensides) =>
        Wrap(nameof(BuildStation), () => { }, pasteroid, pside, pstationtype, now, pbseensides);

    public ThingSite CreateThingSite(IModelIgc pModel) =>
        Wrap(nameof(CreateThingSite),
            () => _default.CreateThingSite(pModel), pModel);
            
    public ClusterSite CreateClusterSite(IClusterIgc pCluster) =>
        Wrap(nameof(CreateClusterSite),
            () => _default.CreateClusterSite(pCluster), pCluster);

    // ── chat ──────────────────────────────────────────────────────────────
    public void ReceiveChat(
        IShipIgc pshipSender, ChatTarget ctRecipient, short oidRecipient,
        short voiceOver, string szText, short cid,
        IgcObjectType otTarget, short oidTarget, IModelIgc? pmodelTarget) =>
        Wrap(nameof(ReceiveChat), () => { },
            pshipSender, ctRecipient, oidRecipient, voiceOver,
            szText is null ? "null" : $"\"{szText}\"", cid, otTarget, oidTarget,
            pmodelTarget);

    public void SendChat(
        IShipIgc pshipSender, ChatTarget chatTarget, short oidRecipient,
        short soVoiceOver, string szText, short cid,
        IgcObjectType otTarget, short oidTarget, IModelIgc? pmodelTarget, bool bObjectModel) =>
        Wrap(nameof(SendChat), () => { },
            pshipSender, chatTarget, oidRecipient, soVoiceOver,
            szText is null ? "null" : $"\"{szText}\"", cid, otTarget, oidTarget,
            pmodelTarget, bObjectModel);

    // ── economy ───────────────────────────────────────────────────────────
    public void GetMoneyRequest(IShipIgc pshipSender, int amount, short hidFor) =>
        Wrap(nameof(GetMoneyRequest), () => { }, pshipSender, amount, hidFor);

    // ── termination / destruction ─────────────────────────────────────────
    public void TerminateModelEvent(IModelIgc model) => Wrap(nameof(TerminateModelEvent), () => { }, model);
    public void TerminateMissionEvent(IMissionIgc pMission) => Wrap(nameof(TerminateMissionEvent), () => { }, pMission);
    public void KillAsteroidEvent(IAsteroidIgc pasteroid, bool explodeF) =>
        Wrap(nameof(KillAsteroidEvent), () => { }, pasteroid, explodeF);
    public void KillAsteroidEvent(short roid, short soid, ISideIgc side) =>
        Wrap(nameof(KillAsteroidEvent), () => { }, roid, soid, side);
    public void DrainAsteroidEvent(IAsteroidIgc pasteroid) =>
        Wrap(nameof(DrainAsteroidEvent), () => { }, pasteroid);
    public void MineAsteroidEvent(IAsteroidIgc pasteroid, float newOre) =>
        Wrap(nameof(MineAsteroidEvent), () => { }, pasteroid, newOre);
    public void KillProbeEvent(IProbeIgc pprobe) => Wrap(nameof(KillProbeEvent), () => { }, pprobe);
    public void KillMissileEvent(IMissileIgc pmissile, Vector3 position) =>
        Wrap(nameof(KillMissileEvent), () => { }, pmissile, position);
    public void KillBuildingEffectEvent(IBuildingEffectIgc pbe) =>
        Wrap(nameof(KillBuildingEffectEvent), () => { }, pbe);
    public void KillMineEvent(IMineIgc pmine) => Wrap(nameof(KillMineEvent), () => { }, pmine);
    public void KillTreasureEvent(ITreasureIgc ptreasure) =>
        Wrap(nameof(KillTreasureEvent), () => { }, ptreasure);

    // ── text ──────────────────────────────────────────────────────────────
    public void PostPlainText(bool bCritical, string pszText) =>
        Wrap(nameof(PostPlainText), () => { }, bCritical, pszText is null ? "null" : $"\"{pszText}\"");

    // ── player / ship events ──────────────────────────────────────────────
    public void EjectPlayer(IModelIgc pmodelCredit) => Wrap(nameof(EjectPlayer), () => { }, pmodelCredit);
    public void TargetKilled(IModelIgc pmodel) => Wrap(nameof(TargetKilled), () => { }, pmodel);
    public void ShipWarped(IShipIgc pship, short sidOld, short sidNew) =>
        Wrap(nameof(ShipWarped), () => { }, pship, sidOld, sidNew);
    public void DamageShipEvent(
        double now, IShipIgc ship, IModelIgc? launcher,
        byte type, float amount, float leakage, Vector3 p1, Vector3 p2) =>
        Wrap(nameof(DamageShipEvent), () => { }, now, ship, launcher, type, amount, leakage, p1, p2);
    public void KillShipEvent(
        double now, IShipIgc ship, IModelIgc? launcher,
        float amount, Vector3 p1, Vector3 p2) =>
        Wrap(nameof(KillShipEvent), () => { }, now, ship, launcher, amount, p1, p2);

    // ── station ───────────────────────────────────────────────────────────
    public void DamageStationEvent(IStationIgc station, IModelIgc launcher, byte type, float amount) =>
        Wrap(nameof(DamageStationEvent), () => { }, station, launcher, type, amount);
    public void KillStationEvent(IStationIgc station, IModelIgc launcher, float amount) =>
        Wrap(nameof(KillStationEvent), () => { }, station, launcher, amount);
    public bool DockWithStationEvent(IShipIgc ship, IStationIgc station) =>
        Wrap(nameof(DockWithStationEvent), () => _default.DockWithStationEvent(ship, station), ship, station);
    public void CaptureStationEvent(IShipIgc ship, IStationIgc station) =>
        Wrap(nameof(CaptureStationEvent), () => { }, ship, station);
    public void VictoryByStationsComputed(
        Allegiance.Core.Model.Mission mission, bool changed, Allegiance.Core.Model.Side? winner) =>
        Wrap(nameof(VictoryByStationsComputed), () => { }, mission, changed, winner);
    public bool LandOnCarrierEvent(IShipIgc shipCarrier, IShipIgc ship, float tCollision) =>
        Wrap(nameof(LandOnCarrierEvent), () => _default.LandOnCarrierEvent(shipCarrier, ship, tCollision), shipCarrier, ship, tCollision);
    public bool RescueShipEvent(IShipIgc shipRescued, IShipIgc shipRescuer) =>
        Wrap(nameof(RescueShipEvent), () => _default.RescueShipEvent(shipRescued, shipRescuer), shipRescued, shipRescuer);

    // ── warps / treasures ─────────────────────────────────────────────────
    public void WarpBombEvent(IWarpIgc pwap, IMissileIgc pmissile) =>
        Wrap(nameof(WarpBombEvent), () => { }, pwap, pmissile);
    public void HitWarpEvent(IShipIgc ship, IWarpIgc warp) =>
        Wrap(nameof(HitWarpEvent), () => { }, ship, warp);
    public bool HitTreasureEvent(double now, IShipIgc ship, ITreasureIgc treasure) =>
        Wrap(nameof(HitTreasureEvent), () => _default.HitTreasureEvent(now, ship, treasure), now, ship, treasure);
    public void PaydayEvent(ISideIgc pside, float money) =>
        Wrap(nameof(PaydayEvent), () => { }, pside, money);

    // ── ripcord ───────────────────────────────────────────────────────────
    public void RequestRipcord(IShipIgc ship, IClusterIgc pcluster) =>
        Wrap(nameof(RequestRipcord), () => { }, ship, pcluster);
    public bool ContinueRipcord(IShipIgc ship, IModelIgc pmodel) =>
        Wrap(nameof(ContinueRipcord), () => _default.ContinueRipcord(ship, pmodel), ship, pmodel);
    public bool UseRipcord(IShipIgc ship, IModelIgc pmodel) =>
        Wrap(nameof(UseRipcord), () => _default.UseRipcord(ship, pmodel), ship, pmodel);

    // ── combat firing ─────────────────────────────────────────────────────
    public void FireMissile(IShipIgc pship, IMagazineIgc pmagazine, double timeFired, IModelIgc pTarget, float lockFraction) =>
        Wrap(nameof(FireMissile), () => { }, pship, pmagazine, timeFired, pTarget, lockFraction);
    public void FireExpendable(IShipIgc pship, IDispenserIgc pdispenser, double timeFired) =>
        Wrap(nameof(FireExpendable), () => { }, pship, pdispenser, timeFired);

    // ── side lifecycle ────────────────────────────────────────────────────
    public void CreateSideEvent(ISideIgc pIsideIGC) => Wrap(nameof(CreateSideEvent), () => { }, pIsideIGC);
    public void DestroySideEvent(ISideIgc pIsideIGC) => Wrap(nameof(DestroySideEvent), () => { }, pIsideIGC);

    // ── instrumentation (C# only, see IIgcSite) ──────────────────────────
    public void SideUpdated(ISideIgc side, double now) =>
        Wrap(nameof(SideUpdated), () => { }, side, now);

    // ── cluster-change routing ────────────────────────────────────────────
    public void ChangeStation(IShipIgc pship, IStationIgc pstationOld, IStationIgc pstationNew) =>
        Wrap(nameof(ChangeStation), () => { }, pship, pstationOld, pstationNew);
    public void ChangeCluster(IShipIgc pship, IClusterIgc pclusterOld, IClusterIgc pclusterNew) =>
        Wrap(nameof(ChangeCluster), () => { }, pship, pclusterOld, pclusterNew);
    public void CommandChangedEvent(short i, IShipIgc pship, IModelIgc ptarget, short cid) =>
        Wrap(nameof(CommandChangedEvent), () => { }, i, pship, ptarget, cid);
    public bool HandlePickDefaultOrder(IShipIgc pship) =>
        Wrap(nameof(HandlePickDefaultOrder), () => _default.HandlePickDefaultOrder(pship), pship);

    // ── asset preload / teleport probes ───────────────────────────────────
    public void Preload(string? pszModelName, string? pszFileName) =>
        Wrap(nameof(Preload), () => { }, pszModelName is null ? "null" : $"\"{pszModelName}\"", pszFileName is null ? "null" : $"\"{pszFileName}\"");
    public void ActivateTeleportProbe(IProbeIgc pprobe) => Wrap(nameof(ActivateTeleportProbe), () => { }, pprobe);
    public void DestroyTeleportProbe(IProbeIgc pprobe) => Wrap(nameof(DestroyTeleportProbe), () => { }, pprobe);

    // ── loadout ───────────────────────────────────────────────────────────
    public void LoadoutChangeEvent(IShipIgc pship, IPartIgc ppart, LoadoutChange lc) =>
        Wrap(nameof(LoadoutChangeEvent), () => { }, pship, ppart, lc);

    // ── game-state / building-effect / expendables ────────────────────────
    public void ChangeGameState() => Wrap(nameof(ChangeGameState), () => { });
    public void CreateBuildingEffect(double now, IAsteroidIgc pasteroid, IShipIgc pshipBuilder) =>
        Wrap(nameof(CreateBuildingEffect), () => { }, now, pasteroid, pshipBuilder);
    public void LayExpendable(double now, IExpendableTypeIgc pet, IShipIgc pshipLayer) =>
        Wrap(nameof(LayExpendable), () => { }, now, pet, pshipLayer);

    // ── cluster lookup (defaults degraded to null per interface docs) ─────
    public IClusterIgc? GetCluster(IShipIgc pship, IModelIgc pmodel) =>
        Wrap(nameof(GetCluster), () => _default.GetCluster(pship, pmodel), pship, pmodel);
    public IClusterIgc? GetRipcordCluster(IShipIgc pship, short habm) =>
        Wrap(nameof(GetRipcordCluster), () => _default.GetRipcordCluster(pship, habm), pship, habm);

    // ── reload ────────────────────────────────────────────────────────────
    public bool Reload(IShipIgc pship, ILauncherIgc plauncher, EquipmentType type) =>
        Wrap(nameof(Reload), () => _default.Reload(pship, plauncher, type), pship, plauncher, type);
}
