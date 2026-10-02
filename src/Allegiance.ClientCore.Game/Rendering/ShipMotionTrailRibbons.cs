using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>A single per-ship ribbon strip as the pool enumerates it for
/// rendering — the port's view of native <c>TrailGeo</c>'s rolling vertex
/// history (efgeo.cpp:492-639). <see cref="Centres"/>/<see cref="Rights"/>
/// are the <c>(Sections+1)</c> strip anchors oldest-last: index 0 is the
/// current head (at the ship), index <see cref="Sections"/> the tail. Each
/// section is drawn as a quad between consecutive anchors; its <b>half</b>-width
/// is <c>Rights[k]</c> on each side (left = <c>Centres[k]-Rights[k]</c>,
/// right = <c>Centres[k]+Rights[k]</c>), mirroring native's per-section
/// left/right vertex pair. <see cref="R"/>/<see cref="G"/>/<see cref="B"/> is
/// the ship's real side colour (full intensity; the renderer applies the
/// native's half-intensity and the per-section brightness taper).</summary>
public readonly record struct ShipTrailStrip(
    short ShipId,
    Vector3[] Centres,
    Vector3[] Rights,
    int Sections,
    float R, float G, float B,
    float DTime);

/// <summary>
/// The pure, MonoGame-free per-ship <b>ribbon</b> trail pool for P363 — a
/// faithful port of native <c>TrailGeo</c> (<c>effect/efgeo.cpp:492-661</c>,
/// created by <c>CreateTrailGeo</c>, efgeo.cpp:630-638, which the ship site
/// builds in <c>SetTrailColor</c>, thinggeo.cpp:921-954). Unlike the two
/// particle-puff systems this port already has (P297 damage-vent smoke,
/// P358 engine exhaust), this is genuine continuous <b>strip geometry</b>:
/// a rolling history of recent anchor positions, laid down every
/// <c>TimeDelta</c> (0.5 s), that stretches behind the ship along its path
/// and fades to the tail.
/// </summary>
/// <remarks>
/// <para><b>Native ribbon algorithm (efgeo.cpp:492-661).</b> The ribbon is a
/// strip of up to <c>maxSections = 16</c> quads built from <c>(maxSections+1)·2</c>
/// vertices (each section is a left/right endpoint pair). Every
/// <c>timeDelta = 0.5 s</c> (<c>Evaluate</c>, efgeo.cpp:609-636) the whole
/// history shifts one section back (newest head → older slot) and a new head
/// is placed at the current anchor. The head is always the current anchor
/// <c>± m_vecRightLast</c>; the right vector is refreshed from the ship's
/// orientation only while it is <b>moving</b> (<c>SetPosition</c> sets
/// <c>m_pbooleanMoving = (m_vecPosition != vec)</c> and
/// <c>SetOrientation</c> sets the right to <c>orient.GetRight() · m_radius ·
/// 0.25</c>, thinggeo.cpp:1149-1183) — so a stopped ship keeps its last width
/// while its old sections continue to age out. Colour tapers along the strip:
/// <c>UpdateColors</c> (efgeo.cpp:570-592) makes each section
/// <c>bright = 1 − dtime − k/maxSections</c> (head bright → tail transparent),
/// where <c>dtime = (time − m_timeLast)/(timeDelta·maxSections)</c> — so when
/// the ship stops, the new sections pile at the stationary anchor while the
/// older laid-down ones keep fading, collapsing the trail back into the ship
/// instead of extending (native fades ~8 s = 16·0.5 s).</para>
/// <para><b>Port substitutions (all documented).</b> (1) The anchor is the
/// ship's <b>centre</b> rather than the extracted <c>"trail"</c> hardpoint
/// offset — the same judgement P358 reached for its engine hardpoints: this
/// port renders Meshy-regenerated content hulls whose coordinate frame and
/// per-asset scale differ from the native <c>.mdl</c> geometry, so mapping the
/// native <c>m_vecTrailOffset</c> (thinggeo.cpp:938) onto them is not cleanly
/// tractable across 70+ hulls; a motion trail traced from the ship's centre is
/// the faithful-simple anchor. (2) <c>UpdateColors</c>'s global <c>dtime</c>
/// term is reproduced via the pool clock (<see cref="Advance"/>), so the head
/// dims slightly just before the next section break exactly as native does.
/// (3) Native's traffic light is driven by the real per-ship
/// <c>m_vecPosition != vec</c>; here motion is inferred the same way from the
/// anchor differing from the previous frame's head.</para>
/// </remarks>
public sealed class ShipMotionTrailRibbons
{
    /// <summary>Native <c>maxSections = 16</c> (efgeo.cpp:489).</summary>
    public const int MaxSections = 16;

    /// <summary>Native <c>timeDelta = 0.5 f</c> — seconds between section
    /// advances (efgeo.cpp:490).</summary>
    public const float TimeDelta = 0.5f;

    /// <summary>1/16 — the per-section brightness decrement native applies in
    /// <c>UpdateColors</c> (efgeo.cpp:574-589).</summary>
    public const float PerSectionBrightnessStep = 1f / MaxSections;

    /// <summary>
    /// Native <c>UpdateColors</c> brightness for a section <paramref name="k"/>
    /// (0 = head): <c>bright = 1 − dtime − k/maxSections</c>, clamped ≥ 0
    /// (efgeo.cpp:572-589). <paramref name="dTime"/> is native's
    /// <c>(time − m_timeLast)/(timeDelta·maxSections)</c>. Exposed so the
    /// head-bright → tail-transparent taper is unit-testable; the renderer
    /// (<see cref="ShipMotionTrailRenderer"/>) uses it verbatim.</summary>
    public static float BrightnessFor(float dTime, int k)
    {
        float b = 1f - dTime - k * PerSectionBrightnessStep;
        return b < 0f ? 0f : b;
    }

    private sealed class Ribbon
    {
        public readonly Vector3[] Centres = new Vector3[MaxSections + 1];
        public readonly Vector3[] Rights = new Vector3[MaxSections + 1];
        public int Section;
        public float LastAdvance;
        public bool HasPosition;
        public Vector3 LastRight;
        public float R = 0.5f, G = 0.5f, B = 0.5f;
    }

    private readonly Dictionary<short, Ribbon> _byShip = new();
    private readonly HashSet<short> _touched = new();
    private float _elapsed;

    /// <summary>The pool's monotonic clock in seconds (advanced by <see cref="Advance"/>);
    /// the anchor of the native ribbon's per-frame timing.</summary>
    public float Elapsed => _elapsed;

    /// <summary>Number of ships currently holding a ribbon.</summary>
    public int RibbonCount => _byShip.Count;

    /// <summary>Advance the pool clock by <paramref name="dtSec"/> seconds —
    /// the per-frame counterpart of native's mission <c>Time</c> flowing into
    /// <c>Evaluate</c>.</summary>
    public void Advance(float dtSec) => _elapsed += dtSec;

    /// <summary>
    /// Feed one visible ship's current anchor (the thing-site position), right
    /// (orientation right × radius × 0.25 — the ribbon's half-width per side)
    /// and real side colour. Reproduces native <c>ThingSite::SetPosition</c> /
    /// <c>SetOrientation</c> / <c>SetTrailColor</c>: motion is detected from the
    /// anchor differing from the previous head, and the ribbon right is
    /// refreshed only while moving. Marks the ship touched for
    /// <see cref="PruneUntouched"/>.
    /// </summary>
    public void SetAnchor(short shipId, Vector3 anchor, Vector3 right, float r, float g, float b)
    {
        if (!_byShip.TryGetValue(shipId, out var rib))
        {
            rib = new Ribbon();
            _byShip[shipId] = rib;
        }

        rib.R = r; rib.G = g; rib.B = b;
        bool moving = !rib.HasPosition || anchor != rib.Centres[0];

        if (!rib.HasPosition)
        {
            rib.HasPosition = true;
            rib.LastAdvance = _elapsed;
        }
        else if (_elapsed - rib.LastAdvance > TimeDelta)
        {
            rib.LastAdvance = _elapsed;
            if (rib.Section < MaxSections) rib.Section++;
            // Shift the whole history one slot toward the tail (native's
            // `for(index = m_section*2+1; index > 1; index--) vertices[index] = vertices[index-2]`,
            // efgeo.cpp:621-626) — new head occupies slot 0, old head slot 1, ... .
            for (int i = rib.Section; i > 0; i--)
            {
                rib.Centres[i] = rib.Centres[i - 1];
                rib.Rights[i] = rib.Rights[i - 1];
            }
        }

        if (moving) rib.LastRight = right;
        rib.Centres[0] = anchor;
        rib.Rights[0] = rib.LastRight;
        _touched.Add(shipId);
    }

    /// <summary>
    /// Drop ribbons for ships that were not fed this frame (dead / left the
    /// visible set), then reset the touched tracking for the next frame. Call
    /// once after the frame's <see cref="SetAnchor"/> gather — native tears a
    /// destroyed ship's ThingSite down, so its trail vanishes at once too.
    /// </summary>
    public void PruneUntouched()
    {
        if (_touched.Count == _byShip.Count)
        {
            _touched.Clear();
            return;
        }
        foreach (var id in _byShip.Keys.ToList())
        {
            if (!_touched.Contains(id))
                _byShip.Remove(id);
        }
        _touched.Clear();
    }

    /// <summary>
    /// Enumerate every live ribbon strip. Skips ships that have never advanced a
    /// section (<c>Sections == 0</c> — a single head point, nothing to draw;
    /// native's <c>RenderCallback</c> guards <c>m_section != 0</c>, efgeo.cpp:
    /// 654). <see cref="ShipTrailStrip.DTime"/> is native's
    /// <c>(time − m_timeLast)/(timeDelta·maxSections)</c> (efgeo.cpp:572) for the
    /// renderer's brightness ramp.
    /// </summary>
    public IEnumerable<ShipTrailStrip> Enumerate()
    {
        foreach (var (id, rib) in _byShip)
        {
            if (!rib.HasPosition || rib.Section == 0) continue;
            float dtime = (_elapsed - rib.LastAdvance) / (TimeDelta * MaxSections);
            yield return new ShipTrailStrip(id, rib.Centres, rib.Rights, rib.Section,
                rib.R, rib.G, rib.B, dtime);
        }
    }

    /// <summary>Drop every ribbon and reset the clock (tests / disconnect).</summary>
    public void Reset()
    {
        _byShip.Clear();
        _touched.Clear();
        _elapsed = 0f;
    }
}
