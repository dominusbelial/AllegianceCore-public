using System;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>The warning families the Jet master-caution panel aggregates (P972 §4.4
/// first paragraph, §5.5 alert rows). Each value is the shared model's own reading.</summary>
public enum JetCautionKind : byte
{
    /// <summary>An enemy missile is tracking the own ship (native
    /// <c>MISSILE_TRACK_WARNING(208)</c> snapshot, <see cref="MissileTrackState"/>).</summary>
    MissileTrack,
    /// <summary>The shared damage flash is lit (<see cref="HudFrame.Flashing"/>,
    /// <c>DamageFlashTracker</c> 0.25/0.25/2.0 s).</summary>
    Damage,
    /// <summary>The own ship is an adrift lifepod (<see cref="HudFrame.IsAdriftLifepod"/>).</summary>
    PodOxygen,
    /// <summary>A store is at the shared critical threshold.</summary>
    CriticalResource,
    /// <summary>A store is at the shared low threshold (the gauge's red band).</summary>
    LowResource,
    /// <summary>Native <c>ShipWarningMask.OutOfBounds</c>.</summary>
    Boundary,
    /// <summary>Native <c>ShipWarningMask.CrowdedSector</c>.</summary>
    Crowd,
    /// <summary>The ghost / pilot-down state (<see cref="HudModel.DeathVisible"/>).</summary>
    Ghost,
    /// <summary>One shared critical tip line (its text is passed through).</summary>
    CriticalTip,
    /// <summary>The shared ripcord countdown label (<see cref="HudContext.Ripcord"/>).</summary>
    Ripcord,
    /// <summary>Native lives badge (<see cref="HudConsoleFormat.LivesVisible"/>).</summary>
    Lives,
    /// <summary>Native deathmatch kills badge (<see cref="HudConsoleFormat.KillsVisible"/>).</summary>
    Kills,
    /// <summary>The active ballot text (<see cref="HudConsoleText.VoteText"/>).</summary>
    Vote,
}

/// <summary>
/// One caution line, resolved from the shared model. <see cref="Fraction"/> /
/// <see cref="Seconds"/> / <see cref="Count"/> carry the numeric value the renderer
/// formats (through its own integer caches, so a steady frame allocates nothing);
/// <see cref="Word"/> and <see cref="Text"/> are only ever existing shared strings (a
/// store word, a critical tip, the vote, the ripcord label) — never a string built here.
/// </summary>
public readonly record struct JetCaution(
    JetCautionKind Kind, int Priority, bool Hot,
    float? Fraction = null, int? Seconds = null, int? Count = null, string? Text = null,
    string? Word = null);


/// <summary>
/// P1038 — the Jet master-caution model (P972 §4.4 + §5.5, phase 3B). It aggregates
/// <b>only existing warnings</b> and never owns a timer, an acknowledgement or a sound:
/// the damage flash timing stays <see cref="Flight.DamageFlashTracker"/>'s
/// 0.25/0.25/2.0 s clock, the missile warning stays the world's
/// <c>MissileTrackState</c> plus the native audio rising edges, the critical tips keep
/// their shared lifetimes and the lives/kills badges keep their native gates.
/// <para><b>Priority</b> (P972 §4.4): missile / damage / pod first, then critical (and
/// low) resources, then boundary / system / lifecycle.</para>
/// </summary>
public static class JetCautionModel
{
    /// <summary>Priority bands, lowest first.</summary>
    public const int PriorityImmediate = 0;   // missile / damage / pod
    public const int PriorityResource = 1;    // critical, then low resources
    public const int PrioritySystem = 2;      // boundary, crowd, ghost, tips, ripcord, badges

    /// <summary>Buffer capacity: one damage line, one missile line, one pod line, five
    /// stores, two boundary/crowd, ghost, four tips, ripcord, two badges, vote.</summary>
    public const int MaxCautions = 18;

    /// <summary>Lines the full panel shows (a stack of three or more must read).</summary>
    public const int FullLines = 5;

    /// <summary>Lines the compact panel shows: P972 §4.5 "caution becomes two hot
    /// lines".</summary>
    public const int CompactLines = 2;

    /// <summary>Panel geometry: top-right, right-aligned at <c>w − 24·scale</c>; the panel's
    /// own top is resolved by the renderer from the measured line height so the stack ends
    /// above the throttle tape's caption at every resolution. The minimum line pitch is
    /// <see cref="LinePitch"/> (the renderer raises it to the measured glyph height).</summary>
    public const float RightInset = 24f;
    public const float LinePitch = 16f;

    public static float Right(float viewportWidth, float scale) => viewportWidth - RightInset * scale;

    public static int LineLimit(bool compact) => compact ? CompactLines : FullLines;

    /// <summary>
    /// Resolve the caution list in priority order into <paramref name="buffer"/>, in a
    /// stable order inside each band. Returns the number of lines written (0 when the
    /// panel is clear). Only readings the shared model supplies are produced: no
    /// telemetry means no resource line, an unknown missile snapshot means no
    /// <c>MSL TRACK</c>, and the mission countdown is absent because
    /// <see cref="HudModel"/> carries no countdown value.
    /// </summary>
    public static int Build(HudModel model, Span<JetCaution> buffer)
    {
        if (buffer.Length == 0) return 0;
        int n = 0;
        if (!model.Visible) return 0;

        // ── priority 0: missile / damage / pod ─────────────────────────────
        if (model.FlightVisible && model.Context.MissileTrack is { IsTracked: true } track)
            Add(buffer, ref n, new(JetCautionKind.MissileTrack, PriorityImmediate, true, Fraction: track.BestLock));
        if (model.InstrumentsVisible && model.Telemetry is { Flashing: true })
            Add(buffer, ref n, new(JetCautionKind.Damage, PriorityImmediate, true));
        if (model.PodVisible && model.Telemetry is { OxygenSecondsRemaining: > 0 } pod)
            Add(buffer, ref n, new(JetCautionKind.PodOxygen, PriorityImmediate, true, Seconds: pod.OxygenSecondsRemaining));

        // ── priority 1: critical resources, then the low ones ──────────────
        if (model.InstrumentsVisible)
        {
            var gauges = model.Gauges;
            // In an eject pod the shared model zeroes the ammo and energy gauges by design
            // (native IsNotEjected, dialog.mdl:208,435,470,507): those zeros are not a
            // resource emergency, so they never raise a caution line.
            bool inPod = model.Telemetry is { IsAdriftLifepod: true };
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < gauges.Count; i++)
                {
                    HudGauge gauge = gauges[i];
                    if (!gauge.Visible || gauge.Kind == GaugeKind.Thrust) continue;
                    if (inPod && gauge.Kind is GaugeKind.Ammo or GaugeKind.Energy) continue;
                    bool critical = JetStoresPanel.IsCritical(gauge.Kind, gauge.Fraction);
                    if (pass == 0 && !critical) continue;
                    if (pass == 1 && (critical || !JetStoresPanel.IsLow(gauge.Kind, gauge.Fraction))) continue;
                    Add(buffer, ref n, new(
                        pass == 0 ? JetCautionKind.CriticalResource : JetCautionKind.LowResource,
                        PriorityResource, critical, Fraction: gauge.Fraction, Word: StoreWord(gauge)));
                }
        }

        // ── priority 2: boundary / system / lifecycle ──────────────────────
        for (int i = 0; i < model.Warnings.Count; i++)
        {
            string warning = model.Warnings[i];
            JetCautionKind kind = warning == HudModel.WarningSectorCrowded ? JetCautionKind.Crowd
                : JetCautionKind.Boundary;
            Add(buffer, ref n, new(kind, PrioritySystem, false));
        }
        if (model.DeathVisible)
            Add(buffer, ref n, new(JetCautionKind.Ghost, PrioritySystem, true));
        // Critical tips keep their shared order and text (newest last in the model).
        if (model.Context.Console is { } console)
        {
            var tips = console.CriticalTips;
            for (int i = 0; i < tips.Count; i++)
                Add(buffer, ref n, new(JetCautionKind.CriticalTip, PrioritySystem, true, Text: tips[i].Text));
            if (HudConsoleFormat.LivesVisible(console.Lives) && console.Lives is { } lives)
                Add(buffer, ref n, new(JetCautionKind.Lives, PrioritySystem, true, Count: lives));
            if (HudConsoleFormat.KillsVisible(console.Deathmatch, console.MissionKills)
                && console.MissionKills is { } kills)
                Add(buffer, ref n, new(JetCautionKind.Kills, PrioritySystem, false, Count: kills));
            if (console.VoteText is { Length: > 0 } vote)
                Add(buffer, ref n, new(JetCautionKind.Vote, PrioritySystem, false, Text: vote));
        }
        if (model.FlightVisible && RipcordSeconds(model.Context.Ripcord) is { } seconds)
            Add(buffer, ref n, new(JetCautionKind.Ripcord, PrioritySystem, false, Seconds: seconds));

        return n;
    }

    /// <summary>A static append (no capturing local function: a closure would allocate a
    /// display class on every frame).</summary>
    private static void Add(Span<JetCaution> buffer, ref int count, JetCaution caution)
    {
        if (count >= buffer.Length) return;
        buffer[count++] = caution;
    }

    /// <summary>The store word of the gauge a resource caution was derived from.</summary>
    private static string StoreWord(in HudGauge gauge)
        => gauge.Kind == GaugeKind.Fuel ? JetStoresPanel.FuelWord(gauge.Label) : JetStoresPanel.StoreWord(gauge.Kind);

    /// <summary>
    /// The seconds of the shared ripcord label (<c>"Ripcord in: 9"</c>,
    /// <see cref="HudConsoleFormat.RipcordLabel"/>), read from the trailing integer so the
    /// panel can print <c>RIP n</c> without a second countdown derivation. Null when the
    /// label carries no number.
    /// </summary>
    public static int? RipcordSeconds(string? label)
    {
        if (label is not { Length: > 0 }) return null;
        int end = label.Length - 1;
        while (end >= 0 && !char.IsAsciiDigit(label[end])) end--;
        if (end < 0) return null;
        int start = end;
        while (start > 0 && char.IsAsciiDigit(label[start - 1])) start--;
        int value = 0;
        for (int i = start; i <= end; i++) value = value * 10 + (label[i] - '0');
        return value;
    }
}

