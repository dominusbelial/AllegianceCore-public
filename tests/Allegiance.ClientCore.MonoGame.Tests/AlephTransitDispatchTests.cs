using System;
using System.Collections.Generic;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P607 — failing-first tests for the aleph-transit dispatch rule
/// (<see cref="Game1.DispatchAlephTransitSignals"/>): the native star swell
/// (<c>WarpRenderer.TriggerTransit</c>) fires for EVERY legitimate visible
/// transit (own/team/enemy/neutral, arrival &amp; departure) regardless of the
/// glimpse flag, while the glimpse funnel-boost/flare pulse is gated by the
/// flag. Fails on pre-P607 code, where the "off" branch discarded the drained
/// signals entirely (an enemy crossing produced no native swell at all).
/// </summary>
public class AlephTransitDispatchTests
{
    private static AlephTransitSignal Sig(short warp, short ship) => new(warp, ship, Time: 100.0);

    /// <summary>A swell recorder that behaves like WarpRenderer.TriggerTransit:
    /// records each triggered warp id (each call is a separate swell event).</summary>
    private sealed class SwellRecorder
    {
        public readonly List<short> Warps = new();
        public void Record(short w) => Warps.Add(w);
        public int Count => Warps.Count;
    }

    [Fact]
    public void FlagOff_EnemyTransit_TriggersExactlyOneSwell_AndNoGlimpsePulse()
    {
        // P607 scope 2: with --aleph-glimpse off, a visible ENEMY transit still
        // swells the native arrival star (exactly once), but the glimpse
        // funnel-boost / flare must NOT fire.
        var swell = new SwellRecorder();
        var pulses = new WarpPulseController();

        Game1.DispatchAlephTransitSignals(
            alephGlimpse: false,
            signals: new[] { Sig(11, ship: 808) },     // an ENEMY ship crossed warp 11
            pulses, swell.Record, pulseLog: null, now: 100.0);

        Assert.Equal(1, swell.Count);                   // exactly ONE native star swell
        Assert.Equal((short)11, swell.Warps[0]);
        Assert.Equal(0, pulses.ActiveCount);            // no glimpse pulse when off
        Assert.Equal(0f, pulses.Strength(11, 100.3));
    }

    [Fact]
    public void FlagOff_TwoSeparateEnemyTransits_SwellTwice_StillNoPulse()
    {
        // Two distinct enemy crossings (different warps) → two swells, still
        // no glimpse pulse. Proves the swell is per-transit, not a one-shot.
        var swell = new SwellRecorder();
        var pulses = new WarpPulseController();

        Game1.DispatchAlephTransitSignals(
            alephGlimpse: false,
            signals: new[] { Sig(11, 801), Sig(12, 807) },
            pulses, swell.Record, pulseLog: null, now: 100.0);

        Assert.Equal(2, swell.Count);
        Assert.Equal(0, pulses.ActiveCount);
    }

    [Fact]
    public void FlagOn_EnemyAndTeamTransits_PulseOnceEach_AndSwellEach()
    {
        // P607/P599: with the flag on, an ENEMY and a TEAM transit each pulse
        // once (two distinct warps → two pulses) and each swells.
        var swell = new SwellRecorder();
        var pulses = new WarpPulseController();
        var logged = new List<AlephTransitSignal>();

        Game1.DispatchAlephTransitSignals(
            alephGlimpse: true,
            signals: new[] { Sig(20, 810), Sig(21, 820) },   // enemy + team
            pulses, swell.Record, logged.Add, now: 100.0);

        Assert.Equal(2, swell.Count);
        Assert.Equal(2, pulses.ActiveCount);                // each pulses once
        Assert.Equal(2, logged.Count);                      // each pulse is logged (the [transit-pulse] line)
        Assert.True(pulses.Strength(20, 100.2) > 0f);
        Assert.True(pulses.Strength(21, 100.2) > 0f);
    }

    [Fact]
    public void FlagOn_SameWarpTwoSources_CoalescesToOnePulse()
    {
        // P599 coalescing preserved: one crossing observed by two paths in the
        // same frame collapses to ONE glimpse pulse (never a flicker/double).
        var swell = new SwellRecorder();
        var pulses = new WarpPulseController();

        Game1.DispatchAlephTransitSignals(
            alephGlimpse: true,
            signals: new[] { Sig(30, 812), Sig(30, 819) },  // same warp, two actors
            pulses, swell.Record, pulseLog: null, now: 100.0);

        Assert.Equal(1, pulses.ActiveCount);                // one warp pulsing, not two stacked
    }

    [Fact]
    public void NoSignals_NeverPulseOrSwell()
    {
        // P599: kills/docks never produce transit signals (ClientWorld-side),
        // and an empty drain must therefore trigger neither swell nor pulse.
        var swell = new SwellRecorder();
        var pulses = new WarpPulseController();

        Game1.DispatchAlephTransitSignals(
            alephGlimpse: true,
            signals: Array.Empty<AlephTransitSignal>(),
            pulses, swell.Record, pulseLog: null, now: 100.0);

        Assert.Equal(0, swell.Count);
        Assert.Equal(0, pulses.ActiveCount);
    }
}
