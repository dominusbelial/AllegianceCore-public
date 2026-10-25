using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Flight;

/// <summary>
/// P813b (EXPERIMENTAL, default OFF) — per-remote-ship positional smoothing by
/// cubic Hermite interpolation of the received <c>ServerShipUpdate</c> states,
/// evaluated on a <b>rendered-late</b> timeline. This is the "remote-ship
/// smoothing on one render clock" idea from the task: instead of rendering a
/// remote ship's extrapolated future pose (the default P448a
/// <see cref="ShipDeadReckoner"/> replays the flight model forward to "now"),
/// the ship is drawn at <c>now − delay</c> where <c>delay</c> is adapted to the
/// ship's own measured update cadence, and the position is the cubic Hermite
/// blend between the two most recent received states straddling that render
/// time — using the <b>sent velocities as tangents</b>. Extrapolation (the
/// existing reckoner) is only ever consulted <em>past the newest sample</em>.
/// </summary>
/// <remarks>
/// <para><b>Why a delayed render beats dead-reckoning (visually).</b> The
/// P448a reckoner extrapolates with the update's <em>stale</em> controls, so at
/// each new update the extrapolated pose jumps onto the freshly-authoritative
/// one — a visible correction spike whose mean/max the world measures
/// (<see cref="State.ClientWorld.TakeRemoteCorrectionStats"/>). Interpolation
/// never extrapolates while it has two bracketing samples, so the drawn pose is
/// <em>always on the arc the server has already published</em>; the residual
/// error is the servo lag of the render delay plus the Hermite tangent fit,
/// which reads as smoothness, not spikes. Its cost is latency (≤ one update
/// interval) — precisely why gunnery and hit-feel do <b>not</b> use this pose
/// (see the P813B report, scope item 3: the P804 lead/aim solver and hit test
/// keep reading the raw server-truth <c>Latest</c> via
/// TargetPaneModel.TryTargetPose).</para>
/// <para><b>Behaviour parity.</b> Native has no delayed-interpolation smoother —
/// its client-side <c>CshipIGC</c> re-runs the flight model forward
/// (<c>ExecuteShipMove</c>, shipIGC.cpp:2885-3037) exactly like the port's
/// default reckoner. This component is therefore a deliberate <b>visual
/// deviation</b> (the brief's "visuals may differ") shipped behind a default-OFF
/// flag; the native-servo analogue it improves upon is the per-update
/// re-seed correction inherent to any forward-replay extrapolator. The Hermite
/// position form (<c>h00 p0 + h10 m0 + h01 p1 + h11 m1</c>, tangents
/// <c>m_i = v_i·span</c>) is the standard Catmull-Rom-from-velocity interpolant
/// referenced by the task's <c>wivuu/stellarallegiance</c> "MotionInterpolator"
/// glossary entry (ideas only; no code copied).</para>
/// <para><b>Allocation.</b> The ring buffer of samples is fixed at construction
/// (no per-sample heap growth); each <see cref="Sample"/> copies its
/// <see cref="Orientation"/> once on <see cref="Feed"/> (it is a mutable
/// reference type), and <see cref="TryGetPose"/> returns a freshly-built
/// orientation. Hot-path (a per-frame query) allocates no buffers.</para>
/// </remarks>
public sealed class RemoteShipHermiteInterpolator
{
    /// <summary>Ring-buffer depth (samples kept per ship). 8 comfortably covers
    /// any sane cadence down to ~1 Hz against the max render delay.</summary>
    public const int History = 8;

    /// <summary>Minimum render delay, seconds — never render "live" (zero lag)
    /// because interpolation needs the newest sample to already be in hand.</summary>
    public const float MinDelay = 0.030f;

    /// <summary>Maximum render delay, seconds — caps lag even for a stalled
    /// stream so gunnery-adjacent visuals (trails on the smoothed pose) never
    /// diverge absurdly far behind.</summary>
    public const float MaxDelay = 0.750f;

    /// <summary>
    /// Render delay = measured update interval × this factor. 1.0 renders
    /// exactly one update-period behind real time, guaranteeing a bracket of
    /// two received samples for every in-buffer render time (the standard
    /// networked-buffered-interpolation operating point). 0.5 halves the lag
    /// at the cost of shorter interpolation windows; &gt;1 adds headroom against
    /// cadence jitter.
    /// </summary>
    public const float DefaultDelayFactor = 1.0f;

    private struct Sample
    {
        public double T;
        public Vector3 Pos;
        public Vector3 Vel;
        public Orientation Ori;
    }

    private readonly Sample[] _buf = new Sample[History];
    private int _head;      // next write slot
    private int _count;     // valid samples in the buffer
    private float _interval = 0.080f;   // measured cadence (EMA), init ≈ 12.5 Hz
    private bool _haveInterval;

    /// <summary>Configured delay factor (see <see cref="DefaultDelayFactor"/>).</summary>
    public float DelayFactor { get; set; } = DefaultDelayFactor;

    /// <summary>
    /// Render delay (seconds) this ship currently applies:
    /// <see cref="Math.Clamp"/> of <c>measured interval × DelayFactor</c> to
    /// <see cref="MinDelay"/>..<see cref="MaxDelay"/>. The adaptive part — each
    /// ship's delay is sized to its own update cadence, so a 2 Hz straggler and
    /// a 20 Hz escort each get exactly one-period of smoothing lag (the "tiers"
    /// interaction in the brief's scope item 4).
    /// </summary>
    public float RenderDelay =>
        Math.Clamp(_interval * DelayFactor, MinDelay, MaxDelay);

    /// <summary>The measured per-ship update interval (seconds, EMA), or the
    /// init guess before two samples exist.</summary>
    public float UpdateInterval => _interval;

    /// <summary>How many valid samples are currently buffered.</summary>
    public int SampleCount => _count;

    /// <summary>
    /// Append one received state, anchored at local render time <paramref name="t"/>
    /// (the SAME clock the render path queries with). Duplicate or re-ordered
    /// arrivals (non-increasing <paramref name="t"/>) are dropped so the buffer
    /// stays monotonic. Updates the cadence EMA.
    /// </summary>
    public void Feed(Vector3 position, Vector3 velocity, Orientation orientation, double t)
    {
        if (_count > 0)
        {
            int last = (_head - 1 + History) % History;
            if (t <= _buf[last].T) return;   // out-of-order / duplicate → drop
        }
        _buf[_head] = new Sample { T = t, Pos = position, Vel = velocity, Ori = orientation.Copy() };
        _head = (_head + 1) % History;
        if (_count < History) _count++;

        if (_count >= 2)
        {
            int prev = (_head - 2 + History) % History;
            int cur = (_head - 1 + History) % History;
            float dt = (float)(_buf[cur].T - _buf[prev].T);
            if (dt > 0.002f)
                _interval = _haveInterval ? _interval + 0.25f * (dt - _interval) : dt;
            _haveInterval = true;
        }
    }

    /// <summary>Clear the buffer (ship left view / recycle).</summary>
    public void Clear()
    {
        _head = 0;
        _count = 0;
        _interval = 0.080f;
        _haveInterval = false;
    }

    /// <summary>
    /// Evaluate the smoothed pose for render time <paramref name="now"/>.
    /// The effective render instant is <c>now − <see cref="RenderDelay"/></c>.
    /// <list type="bullet">
    /// <item><b>Inside the buffer</b> (≥ 2 samples straddling the render instant):
    /// cubic Hermite position + rotation-slerp orientation, both at the same
    /// fraction <c>u</c> — the single render-clock sample.</item>
    /// <item><b>Before the oldest sample</b> (cold start): hold the oldest pose.</item>
    /// <item><b>Past the newest sample</b> (a stalled stream): returns
    /// <b>false</b> so the caller falls back to the existing dead-reckoner /
    /// raw extrapolation. Only in this regime is anything extrapolated — exactly
    /// the brief's "extrapolate only past the newest sample".</item>
    /// </list>
    /// </summary>
    /// <returns>True when a smoothed (interpolated) pose was produced;
    /// false when the caller must extrapolate/fall back.</returns>
    public bool TryGetPose(double now, out Vector3 position, out Orientation orientation)
    {
        position = default;
        orientation = null!;
        if (_count < 2) return false;

        double rt = now - RenderDelay;
        int newest = (_head - 1 + History) % History;
        if (rt > _buf[newest].T) return false;   // past newest → extrapolate (caller)

        int oldest = (_head - _count + History) % History;
        if (rt <= _buf[oldest].T)
        {
            position = _buf[oldest].Pos;
            orientation = _buf[oldest].Ori.Copy();
            return true;
        }

        // Locate the bracket [a,b] with a.T <= rt <= b.T.
        int a = oldest, b = oldest;
        bool found = false;
        for (int k = 0; k < _count - 1; k++)
        {
            int i = (oldest + k) % History;
            int j = (oldest + k + 1) % History;
            if (_buf[i].T <= rt && rt <= _buf[j].T) { a = i; b = j; found = true; break; }
        }
        if (!found) { position = _buf[newest].Pos; orientation = _buf[newest].Ori.Copy(); return true; }

        double t0 = _buf[a].T, t1 = _buf[b].T;
        double span = t1 - t0;
        if (span <= 1e-9)
        {
            position = _buf[a].Pos;
            orientation = _buf[a].Ori.Copy();
            return true;
        }
        double u = (rt - t0) / span;
        Hermite3(_buf[a].Pos, _buf[a].Vel * (float)span,
                 _buf[b].Pos, _buf[b].Vel * (float)span, (float)u, out position);
        orientation = OrientationSlerp(_buf[a].Ori, _buf[b].Ori, (float)u);
        return true;
    }

    /// <summary>
    /// Cubic Hermite position with tangents <c>m0, m1</c> (here <c>v·span</c>):
    /// <c>p(u)=h00 p0 + h10 m0 + h01 p1 + h11 m1</c>, the standard basis
    /// <c>h00=(1+2u)(1-u)², h10=u(1-u)², h01=u²(3-2u), h11=u²(u-1)</c>.
    /// </summary>
    private static void Hermite3(in Vector3 p0, in Vector3 m0, in Vector3 p1, in Vector3 m1, float u, out Vector3 result)
    {
        float u2 = u * u, u3 = u2 * u;
        float h00 = 2f * u3 - 3f * u2 + 1f;
        float h10 = u3 - 2f * u2 + u;
        float h01 = -2f * u3 + 3f * u2;
        float h11 = u3 - u2;
        result = p0 * h00 + m0 * h10 + p1 * h01 + m1 * h11;
    }

    /// <summary>
    /// Constant-angular-rate rotation blend between two orientations (the
    /// rotation-geodesic "slerp" without a quaternion type): relative rotation
    /// <c>R = a⁻¹·b</c> in <c>a</c>'s frame is reduced to an axis-angle, and
    /// <c>a</c> is <see cref="Orientation.PostRotate"/>d about that axis by
    /// <c>angle·t</c> (renormalizing, exactly native's model update). This is
    /// the shortest-path blend and matches how a ship's orientation visibly
    /// turns between updates.
    /// </summary>
    private static Orientation OrientationSlerp(in Orientation a, in Orientation b, float t)
    {
        if (t <= 0f) return a.Copy();
        if (t >= 1f) return b.Copy();

        // Express b's world axes in a's local frame → columns of R_local.
        Vector3 rx = a.TimesInverse(b.GetRight());
        Vector3 ry = a.TimesInverse(b.GetUp());
        Vector3 rz = a.TimesInverse(b.GetBackward());

        float trace = rx.X + ry.Y + rz.Z;
        float cosTheta = Math.Clamp(0.5f * (trace - 1f), -1f, 1f);
        float theta = (float)Math.Acos(cosTheta);
        if (theta < 1e-4f) return a.Copy();   // essentially identical

        // Axis from the skew-symmetric part of R_local (R − Rᵀ)/2.
        Vector3 axis = new(
            rz.Y - ry.Z,
            rx.Z - rz.X,
            ry.X - rx.Y);
        float len = axis.Length();
        if (len < 1e-6f) return a.Copy();
        axis /= len;

        var result = a.Copy();
        result.PostRotate(axis, theta * t);
        return result;
    }
}
