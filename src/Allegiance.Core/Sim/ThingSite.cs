namespace Allegiance.Core.Sim;

/// <summary>Per-object host identity and visibility attributes. Native
/// FedSrv/FedSrv.CPP:10692-10709; scanner membership belongs to the cluster.</summary>
public class ThingSite
{
    public IModelIgc? Owner { get; init; }
    private SideVisibilityState? _visibility;
    // The native constructor starts with a fresh visibility array, even if a
    // previous site for this model had seen contacts. Allocate lazily so real
    // placement can bind the shared model state without an extra allocation.
    public SideVisibilityState? Visibility => Owner is ISideVisible
        ? _visibility ??= new SideVisibilityState { Predictable = Predictable } : null;
    internal void BindVisibility(SideVisibilityState state) => _visibility = state;
    public bool Predictable { get; }
    public bool TracksSideVisibility { get; }

    public ThingSite() { }
    public ThingSite(IModelIgc model)
    {
        Owner = model;
        Predictable = model is Chaff || ((model as ISideVisible)?.SideVisibility.Predictable ?? false);
        TracksSideVisibility = model is Ship or Station or Asteroid or Probe or Mine or Treasure
            or Missile or Allegiance.Core.Model.ResolvedWarp;
    }

    // Native ThingSiteImpl::Terminate and destructor are empty (:10705-10709).
    // Releasing the managed owner does not erase visibility on a retained model.
}
