namespace Allegiance.ClientCore.State;

/// <summary>
/// P374 — a transient one-shot sector-threat cue recorded by
/// <see cref="ClientWorld.Apply(Allegiance.Core.Wire.ClusterWarningUpdate)"/>
/// when a side's per-sector threat state changed in a way that must SOUND
/// (native <c>ClusterSiteImpl::UpdateClusterWarnings</c>, trekigc.cpp:327-383).
/// Mirrors the P373 <see cref="ClientNewContactCue"/> lifecycle: appended under
/// the gate lock, drained once per frame by <c>Game1</c> via
/// <see cref="ClientWorld.TakeClusterWarningCues"/> into the audio manager.
/// </summary>
/// <param name="SectorId">The sector whose threat state changed.</param>
/// <param name="Warning">The new current <see cref="ClusterWarnings"/> level
/// (meaningful only for an alert cue; the alert fires when this is the INCREASE
/// edge above <see cref="ClusterWarnings.NoThreat"/>).</param>
/// <param name="Secured">True when this is the sector-secured transition
/// (<c>sectorSecuredSound</c> + "<c>{name} secured.</c>").</param>
/// <param name="Lost">True when this is the sector-lost transition
/// (<c>sectorLostSound</c> + "<c>{name} lost.</c>").</param>
public readonly record struct ClientClusterWarningCue(
    short SectorId, short Warning, bool Secured, bool Lost);
