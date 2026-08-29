namespace Allegiance.Core.Sim;

// Marker interfaces for the IGC object-pointer parameters of IIgcSite, so the
// seam's signatures are faithful to src/Igc/igc.h now and can be fleshed out
// when the object model arrives. Deliberately EMPTY placeholders — no invented
// members (per task rule). Naming drops the C++ I*IGC style for IShipIgc etc.,
// matching the existing C# IgcObjectType convention.

/// <summary>Placeholder for <c>ImodelIGC</c>. Empty — do not invent members.</summary>
public interface IModelIgc;
/// <summary>Placeholder for <c>IclusterIGC</c>. Empty — do not invent members.</summary>
public interface IClusterIgc;
/// <summary>Placeholder for <c>IshipIGC</c>. Empty — do not invent members.</summary>
public interface IShipIgc : IModelIgc;
/// <summary>Placeholder for <c>IstationIGC</c>. Empty — do not invent members.</summary>
public interface IStationIgc : IModelIgc;
/// <summary>Placeholder for <c>IasteroidIGC</c>. Empty — do not invent members.</summary>
public interface IAsteroidIgc : IModelIgc;
/// <summary>Placeholder for <c>ItreasureIGC</c>. Empty — do not invent members.</summary>
public interface ITreasureIgc : IModelIgc;
/// <summary>Placeholder for <c>IpartIGC</c>. Empty — do not invent members.</summary>
public interface IPartIgc;
/// <summary>Placeholder for <c>IbucketIGC</c>. Empty — do not invent members.</summary>
public interface IBucketIgc;
/// <summary>Placeholder for <c>IdevelopmentIGC</c>. Empty — do not invent members.</summary>
public interface IDevelopmentIgc;
/// <summary>Placeholder for <c>IstationTypeIGC</c>. Empty — do not invent members.</summary>
public interface IStationTypeIgc;
/// <summary>Placeholder for <c>IhullTypeIGC</c>. Empty — do not invent members.</summary>
public interface IHullTypeIgc;
/// <summary>Placeholder for <c>IsideIGC</c>. Empty — do not invent members.</summary>
public interface ISideIgc;
/// <summary>Placeholder for <c>IpartTypeIGC</c>. Empty — do not invent members.</summary>
public interface IPartTypeIgc;
/// <summary>Placeholder for <c>IdroneTypeIGC</c>. Empty — do not invent members.</summary>
public interface IDroneTypeIgc;
/// <summary>Placeholder for <c>IprobeIGC</c>. Empty — do not invent members.</summary>
public interface IProbeIgc : IModelIgc;
/// <summary>Placeholder for <c>ImissileIGC</c>. Empty — do not invent members.</summary>
public interface IMissileIgc : IModelIgc;
/// <summary>Placeholder for <c>IbuildingEffectIGC</c>. Empty — do not invent members.</summary>
public interface IBuildingEffectIgc : IModelIgc;
/// <summary>Placeholder for <c>ImineIGC</c>. Empty — do not invent members.</summary>
public interface IMineIgc : IModelIgc;
/// <summary>Placeholder for <c>IwarpIGC</c>. Empty — do not invent members.</summary>
public interface IWarpIgc;
/// <summary>Placeholder for <c>ImagazineIGC</c>. Empty — do not invent members.</summary>
public interface IMagazineIgc : IModelIgc;
/// <summary>Placeholder for <c>IdispenserIGC</c>. Empty — do not invent members.</summary>
public interface IDispenserIgc : IModelIgc;
/// <summary>Placeholder for <c>IlauncherIGC</c>. Empty — do not invent members.</summary>
public interface ILauncherIgc : IModelIgc;
/// <summary>Placeholder for <c>IexpendableTypeIGC</c>. Empty — do not invent members.</summary>
public interface IExpendableTypeIgc;
/// <summary>Placeholder for <c>ImissionIGC</c>. Empty — do not invent members.</summary>
public interface IMissionIgc;
