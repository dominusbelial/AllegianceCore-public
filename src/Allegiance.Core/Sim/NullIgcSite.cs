namespace Allegiance.Core.Sim;

/// <summary>
/// An <see cref="IIgcSite"/> that takes every default — a mission bound to this
/// site is legal but hears nothing. This is the analogue of the raw C++
/// <c>IIgcSite</c> instantiation, and the site used to prove a mission can be
/// assembled with a compliant (if mute) host. It records nothing; native methods run their interface defaults.
/// The port adapter retains queued ripcord completions for bare-domain hosts.
/// </summary>
public sealed class NullIgcSite : IIgcSite
{
    // Bare-domain missions preserve the sector's completion-drain adapter.
    public bool QueueRipcordUse => true;

    // All native methods use defaults: every method falls through to the interface's default
    // implementation (no-op, or the identified-safe return). Nothing to do here
    // now or as the seam grows — a NullIgcSite is meant to be thrown away.
}
