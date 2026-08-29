namespace Allegiance.Core.Sim;

/// <summary>
/// An <see cref="IIgcSite"/> that takes every default — a mission bound to this
/// site is legal but hears nothing. This is the analogue of the raw C++
/// <c>IIgcSite</c> instantiation, and the site used to prove a mission can be
/// assembled with a compliant (if mute) host. It records nothing and overrides
/// nothing; every member runs the interface default.
/// </summary>
public sealed class NullIgcSite : IIgcSite
{
    // Deliberately empty: every method falls through to the interface's default
    // implementation (no-op, or the identified-safe return). Nothing to do here
    // now or as the seam grows — a NullIgcSite is meant to be thrown away.
}
