using System.Numerics;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// Lazy resolution of "the player ship's current command target" — the C# port of the native
/// <c>CurrentTarget(new PlayerShipTarget, lockOnFirstFetch)</c> wrapper (<c>currenttarget.cpp:23-57</c>,
/// <c>playershiptarget.cpp:30-34</c>). <c>PlayerShipTarget</c> resolves to the player's ship; the
/// <c>CurrentTarget</c> wrapper then dereferences that ship's <c>GetCommandTarget(c_cmdCurrent)</c>.
/// With <paramref name="lockOnFirstFetch"/> it caches the first-fetched target and, unlike a plain
/// cache, re-fetches if the player changes target without the original being eliminated
/// (<c>currenttarget.cpp:39-49</c>).
/// </summary>
public sealed class PlayerCurrentTarget
{
    private readonly ITrainingSite _site;
    private readonly bool _lock;
    private TargetRef? _fetched;

    public PlayerCurrentTarget(ITrainingSite site, bool lockOnFirstFetch = false)
    {
        _site = site;
        _lock = lockOnFirstFetch;
    }

    /// <summary>Resolve to the player's current command-target id, or <c>null</c> when none.</summary>
    public TargetRef? Resolve()
    {
        var cur = _site.GetCommandTarget(_site.PlayerShip.Ref, CommandCurrent);
        if (!_lock)
            return cur;
        if (_fetched is not { } f)
        {
            _fetched = cur;
        }
        else if (cur is { } c && (c.Type != f.Type || c.Id != f.Id) && _site.Resolve(f) is not null)
        {
            // Target changed while the fetched one is still alive — re-anchor
            // (currenttarget.cpp:39-49: `pFetched` dereferences to a live model, so an
            // eliminated original stays latched and the destroy condition observes it).
            _fetched = cur;
        }
        return _fetched;
    }
}

/// <summary>
/// TRUE when a ship is destroyed — port of the <c>GetShipIsDestroyedCondition</c> macro
/// (<c>getshipisdestroyedcondition.h:27</c>): <c>Not(GetShipIsDamagedCondition(ship, 0.0f))</c>.
/// GetShipIsDamagedCondition(ship, 0.0) is true iff the ship exists with armour fraction ≠ 0
/// (<c>getshipisdamagedcondition.cpp:44-57</c>); its negation is true when the ship is gone <em>or</em>
/// its armour fraction is exactly 0. When the (possibly lazy) target resolves to nothing, the
/// condition is true — matching native's "assume 0 since there is no ship"
/// (<c>getshipisdamagedcondition.cpp:53-55</c>).
/// </summary>
public sealed class GetShipIsDestroyedCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly Func<TargetRef?> _target;

    public GetShipIsDestroyedCondition(ITrainingSite site, TargetRef target)
        : this(site, () => target) { }

    public GetShipIsDestroyedCondition(ITrainingSite site, PlayerCurrentTarget target)
        : this(site, target.Resolve) { }

    private GetShipIsDestroyedCondition(ITrainingSite site, Func<TargetRef?> target)
    {
        _site = site;
        _target = target;
    }

    public override bool Evaluate()
        => _target() is not { } t || _site.IsShipDestroyed(t);
}

/// <summary>
/// TRUE when a specific <c>(type, id)</c> ship is destroyed — port of the
/// <c>GetShipIsDestroyedCondition2(shipType, shipID)</c> macro (<c>getshipisdestroyedcondition.h:28</c>).
/// </summary>
public sealed class GetShipIsDestroyedCondition2 : Condition
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;

    public GetShipIsDestroyedCondition2(ITrainingSite site, ObjectType type, int shipId)
    {
        _site = site;
        _ship = new TargetRef(type, shipId);
    }

    public override bool Evaluate() => _site.IsShipDestroyed(_ship);
}

/// <summary>
/// TRUE when a ship's accepted-or-current command id equals an expected command — port of
/// <c>GetCommandCondition</c> (<c>getcommandcondition.cpp:27-54</c>): returns true if the ship has the
/// command (accepted OR current), and true if the ship is absent (<c>getcommandcondition.cpp:53</c>).
/// </summary>
public sealed class GetCommandCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;
    private readonly int _command;

    public GetCommandCondition(ITrainingSite site, TargetRef ship, int command)
    {
        _site = site;
        _ship = ship;
        _command = command;
    }

    public override bool Evaluate() => _site.HasCommand(_ship, _command);
}

/// <summary>
/// TRUE when a model is in a given sector — port of <c>GetSectorCondition</c>
/// (<c>getsectorcondition.cpp:27-51</c>): the model's current sector id equals the expected one;
/// false when the model is absent.
/// </summary>
public sealed class GetSectorCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly Func<TargetRef?> _model;
    private readonly int _sector;

    public GetSectorCondition(ITrainingSite site, TargetRef model, int sector)
        : this(site, () => model, sector) { }

    public GetSectorCondition(ITrainingSite site, PlayerCurrentTarget model, int sector)
        : this(site, model.Resolve, sector) { }

    private GetSectorCondition(ITrainingSite site, Func<TargetRef?> model, int sector)
    {
        _site = site;
        _model = model;
        _sector = sector;
    }

    public override bool Evaluate()
        => _model() is { } m && _site.GetSector(m) == _sector;
}

/// <summary>
/// TRUE when a ship has a target in scanner range — port of <c>ShipSeesObjectCondition</c>
/// (<c>shipseesobjectcondition.cpp:29-51</c>): <c>IshipIGC::InScannerRange(target)</c>.
/// </summary>
public sealed class ShipSeesObjectCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;
    private readonly TargetRef _target;

    public ShipSeesObjectCondition(ITrainingSite site, TargetRef ship, TargetRef target)
    {
        _site = site;
        _ship = ship;
        _target = target;
    }

    public override bool Evaluate() => _site.ShipSees(_ship, _target);
}

/// <summary>
/// Damages the player's ship — port of <c>DoDamageAction</c> (<c>dodamageaction.cpp:24-50</c>). With
/// <c>toHull=false</c> it dents the shield (3% of max, only while shield &gt; 20%); with
/// <c>toHull=true</c> it drops the shield to 20% (while &gt;25%) or hits the hull by 7.5 (while &gt;50%).
/// </summary>
public sealed class DoDamageAction : Action
{
    private readonly ITrainingSite _site;
    private readonly bool _toHull;

    public DoDamageAction(ITrainingSite site, bool toHull)
    {
        _site = site;
        _toHull = toHull;
    }

    public override void Execute() => _site.DealDamage(_toHull);
}

/// <summary>
/// Damages a specific ship by a collision amount, then resets its controls and fires all its
/// weapons — port of <c>DoDamageAdjustableAction</c> (<c>dodamageadjustableaction.cpp:23-36</c>):
/// <c>ReceiveDamage(c_dmgidCollision, amount, …)</c> then
/// <c>SetStateBits(weaponsMaskIGC|buttonsMaskIGC, allWeaponsIGC)</c> and <c>SetControls</c> reset.
/// </summary>
public sealed class DoDamageAdjustableAction : Action
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;
    private readonly float _amount;

    public DoDamageAdjustableAction(ITrainingSite site, TargetRef ship, float amount)
    {
        _site = site;
        _ship = ship;
        _amount = amount;
    }

    public override void Execute() => _site.DamageShipAdjustable(_ship, _amount);
}

/// <summary>
/// TRUE when a ship's ammunition is <em>below</em> the requested count despite the
/// "HasAmmo" name — port of the native bug in <c>GetShipHasAmmoCondition</c>
/// (<c>getshiphasammocondition.cpp:32-34</c>: <c>GetAmmo() &gt;= count ? false : true</c>).
/// Kept native (P873 lists the inverted meaning; the brief says keep native semantics).
/// </summary>
public sealed class GetShipHasAmmoCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;
    private readonly int _count;

    public GetShipHasAmmoCondition(ITrainingSite site, TargetRef ship, int count = -1)
    {
        _site = site;
        _ship = ship;
        _count = count;
    }

    public override bool Evaluate() => _site.GetAmmo(_ship) < _count;
}

/// <summary>
/// Toggles a weapon mount — port of <c>ToggleWeaponAction</c> (<c>toggleweaponaction.cpp:28-47</c>);
/// native forwards to the window's <c>ToggleWeapon</c>.
/// </summary>
public sealed class ToggleWeaponAction : Action
{
    private readonly ITrainingSite _site;
    private readonly int _mount;

    public ToggleWeaponAction(ITrainingSite site, int mount)
    {
        _site = site;
        _mount = mount;
    }

    public override void Execute() => _site.ToggleWeapon(_mount);
}

/// <summary>
/// Swaps a mounted part of an equipment type to a mount — port of <c>SwapWeaponAction</c>
/// (<c>swapweaponaction.cpp:28-37</c>); native forwards to the window's <c>SwapPart</c>.
/// Equipment types are the native <c>ET_*</c> (<c>etMagazine = 3</c>, weapon = 0).
/// </summary>
public sealed class SwapWeaponAction : Action
{
    private readonly ITrainingSite _site;
    private readonly int _equipmentType;
    private readonly int _mount;

    public SwapWeaponAction(ITrainingSite site, int equipmentType, int mount)
    {
        _site = site;
        _equipmentType = equipmentType;
        _mount = mount;
    }

    public override void Execute() => _site.SwapWeapon(_equipmentType, _mount);
}

/// <summary>
/// Heals and re-arms the player's ship — port of <c>RepairRearmAction</c>
/// (<c>repairrearraction.cpp:34-74</c>): shield+hull to 1, energy/ammo/fuel to max, then fills the
/// first two empty cargo slots with missiles (150) and ammo (24) and the rest with missiles.
/// </summary>
public sealed class RepairRearmAction : Action
{
    private readonly ITrainingSite _site;

    public RepairRearmAction(ITrainingSite site) => _site = site;

    public override void Execute() => _site.RepairAndRearm();
}

/// <summary>
/// Shows a client pane, turning off the sibling panes — port of <c>ShowPaneAction</c>
/// (<c>showpaneaction.cpp:28-38</c>): turns off teleport/investment/gamestate/team/expanded/inventory/
/// command/sector panes, then on the requested mask. Mask ids are <c>PaneInventory</c>/<c>PaneSector</c>
/// (<c>console.h:48-55</c>).
/// </summary>
public sealed class ShowPaneAction : Action
{
    private readonly ITrainingSite _site;
    private readonly int _paneMask;

    public ShowPaneAction(ITrainingSite site, int paneMask)
    {
        _site = site;
        _paneMask = paneMask;
    }

    public override void Execute() => _site.ShowPane(_paneMask);
}

/// <summary>
/// Shows/hides the enemy-detection eyeball — port of <c>ShowEyeballAction</c>
/// (<c>showeyeballaction.cpp:28-35</c>): sets the player ship-status "detected" flag.
/// </summary>
public sealed class ShowEyeballAction : Action
{
    private readonly ITrainingSite _site;
    private readonly bool _shown;

    public ShowEyeballAction(ITrainingSite site, bool shown)
    {
        _site = site;
        _shown = shown;
    }

    public override void Execute() => _site.SetEyeball(_shown);
}

/// <summary>
/// Sets an object's scan signature — port of <c>SetSignatureAction</c>
/// (<c>setsignatureaction.cpp:29-49</c>): <c>model-&gt;SetSignature(sig)</c>.
/// </summary>
public sealed class SetSignatureAction : Action
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _target;
    private readonly float _signature;

    public SetSignatureAction(ITrainingSite site, ObjectType type, int id, float signature)
    {
        _site = site;
        _target = new TargetRef(type, id);
        _signature = signature;
    }

    public override void Execute() => _site.SetSignature(_target.Type, _target.Id, _signature);
}
