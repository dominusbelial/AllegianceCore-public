using Allegiance.Core.Sim;

namespace Allegiance.Core.Collision;

/// <summary>
/// Result of testing one candidate pair over a step. <see cref="Found"/> is
/// false when the pair does not collide within the step.
/// </summary>
public readonly record struct CollisionTest(bool Found, float TCollision, int HtsA, int HtsB)
{
    public static CollisionTest Miss => new(false, 0f, HitShape.Sphere, HitShape.Sphere);
}

/// <summary>
/// The narrow phase — a faithful port of <c>HitTest::Collide</c>
/// (<c>src/_Utility/HitTest.cpp:114-206</c>) and its helpers:
/// <c>HullCollide</c> (<c>:921-1043</c>), <c>IntervalCollide</c>
/// (<c>:1045-1136</c>), <c>DoGilbert</c>/GJK (<c>:1858-1948</c>) and the
/// Johnson simplex closest-point routines (<c>:1226-1856</c>).
/// </summary>
public static class NarrowPhase
{
    private const double Epsilon = 1.0 / 128.0;
    private const double CMaxDelta = 0.25;

    /// <summary>
    /// Test two colliders over a step <c>[0, dt]</c> relative to the step's start
    /// (a static body sits at its position). Returns the first-contact time and
    /// the concrete sub-shapes used, or <see cref="CollisionTest.Miss"/>. The C++
    /// <c>Collide</c> entry (<c>HitTest.cpp:114-206</c>).
    /// </summary>
    public static CollisionTest TestPair(Collider a, Collider b, float dt)
    {
        const float tStart = 0f;
        float tStop = dt;
        if (!(tStop > tStart))
            throw new ArgumentOutOfRangeException(nameof(dt), "step duration must be positive");

        var dP = a.Position - b.Position;
        float r = b.EffectiveRadius + a.EffectiveRadius;
        double c = (double)(dP * dP) - (double)(r * r);
        var dV = b.Velocity - a.Velocity;

        bool fCollision = false;
        float tCollision = 0f;
        float tMax = 0f;

        double halfB = (dP * dV);
        if (halfB > 0.0)
        {
            double aq = dV * dV;           // quadratic coefficient
            if (aq > 0.0)
            {
                double b2ac = halfB * halfB - aq * c;
                if (b2ac >= 0.0)
                {
                    double s = Math.Sqrt(b2ac);
                    tCollision = c <= 0.0 ? 0.0f : (float)((halfB - s) / aq);
                    tMax = (float)((halfB + s) / aq);
                    if (tMax > tStop) tMax = tStop;
                    fCollision = true;
                }
            }
        }
        else if (c < 0.0)
        {
            // Overlapping at the start (possible even when not approaching).
            tCollision = tStart;
            tMax = float.MaxValue;
            if (halfB != 0.0)
            {
                double aq = dV * dV;
                if (aq > 0.0)
                    tMax = (float)((halfB + Math.Sqrt(halfB * halfB - aq * c)) / aq);
            }
            if (tMax > tStop) tMax = tStop;
            fCollision = true;
        }

        if (fCollision && tCollision >= tStart && tCollision <= tStop + (float)Epsilon)
        {
            int htsThis = a.Shape;
            int htsOther = b.Shape;
            if ((a.Shape <= HitShape.Sphere) && (b.Shape <= HitShape.Sphere))
                return new CollisionTest(true, tCollision, htsThis, htsOther);

            var dPv = dP; var dVv = dV;
            var hull = HullCollide(ref tCollision, tMax + (float)Epsilon, a, b, dPv, dVv);
            if (hull.Found)
                return new CollisionTest(true, hull.TCollision, hull.HtsA, hull.HtsB);
            return CollisionTest.Miss;
        }

        return CollisionTest.Miss;
    }

    private static (bool Found, float TCollision, int HtsA, int HtsB) HullCollide(
        ref float tStart, float tMax, Collider a, Collider b, Vector3 dP, Vector3 dV)
    {
        // Express everything in B's local coordinate space (dP/dV already wrt B).
        // o = oA * oB^-1 gives A's orientation in B's frame.
        var oB = b.Orientation;
        var oA = a.Orientation;
        var o = ComposeInverse(oA, oB);
        Vector3 p = oB.TimesInverse(dP);
        Vector3 v = oB.TimesInverse(dV);

        double speed = dV.Length();
        float maxDeltaT = speed == 0.0 ? float.MaxValue : (float)(CMaxDelta / speed);

        int htsA = a.Shape;
        int htsB = b.Shape;
        int htsAcurrent = htsA < 0 ? htsA : 0;
        float tOriginalStart = tStart;
        bool bCollision = false;
        int resultHtsA = htsA, resultHtsB = htsB;
        float resultT = tStart;

        do
        {
            int htsBcurrent = htsB < 0 ? htsB : 0;
            do
            {
                if (IntervalCollide(tOriginalStart, tMax, maxDeltaT, a, htsAcurrent, b, htsBcurrent,
                                    p, v, o, ref tStart))
                {
                    bCollision = true;
                    resultHtsA = htsAcurrent;
                    resultHtsB = htsBcurrent;
                    resultT = tStart;
                    tMax = tStart;   // no point in collisions after hitting this part
                }
            }
            while (++htsBcurrent < htsB);
        }
        while (++htsAcurrent < htsA);

        return bCollision ? (true, resultT, resultHtsA, resultHtsB) : (false, 0f, htsA, htsB);
    }

    /// <summary>The C++ <c>IntervalCollide</c> (<c>HitTest.cpp:1045-1136</c>).</summary>
    private static bool IntervalCollide(float tStart, float tStop, float maxDeltaT,
        Collider a, int htsA, Collider b, int htsB,
        Vector3 dP, Vector3 dV, Orientation orientationA, ref float tCollision)
    {
        float tMiddle = (tStart + tStop) / 2f;

        Vector3 direction = dP - tMiddle * dV;
        if (htsA >= HitShape.ConvexHullMin) direction += a.GetCenter(htsA) * orientationA;
        if (htsB >= HitShape.ConvexHullMin) direction -= b.GetCenter(htsB);
        if (direction.X == 0f && direction.Y == 0f && direction.Z == 0f) return true;

        direction = direction.Normalize();
        Vector3 positionStart = dP - tStart * dV;
        Vector3 positionStop = dP - tStop * dV;

        const int cMaxAttempts = 4;
        var extremesA = new Vector3[cMaxAttempts];
        var extremesB = new Vector3[cMaxAttempts];
        int attempt = 0;
        while (true)
        {
            extremesA[attempt] = a.GetMinExtreme(htsA,
                (direction * dV) >= 0f ? positionStop : positionStart,
                orientationA, direction);
            extremesB[attempt] = b.GetMaxExtreme(htsB, direction);

            Vector3 delta = extremesA[attempt] - extremesB[attempt];
            double distance = delta * direction;
            if (distance > 0.0)
                return false;                       // separating plane found
            else if (attempt++ >= cMaxAttempts - 1)
                break;

            // Reflect direction about the projection onto -delta to try a better plane.
            double denom = delta.LengthSquared();
            if (denom == 0.0) break;
            direction -= delta * (float)(2.0 * (direction * delta) / denom);
        }

        // Four attempts failed to separate: invoke Gilbert (GJK) with the simplex.
        var simplex = new Vector3[4];
        for (int i = 0; i < 4; i++)
            simplex[i] = extremesA[i] - extremesB[i];

        Vector3 positionStartA = positionStart;
        Vector3 positionStopA = positionStop;
        Vector3 dVa = dV;
        if (!DoGilbert(a, htsA, positionStartA, positionStopA, dVa, orientationA, b, htsB, simplex))
            return false;

        if (tStop - tStart <= maxDeltaT)
        {
            tCollision = tMiddle;
            return true;
        }
        else
            return IntervalCollide(tStart, tMiddle, maxDeltaT, a, htsA, b, htsB, dP, dV, orientationA, ref tCollision)
                || IntervalCollide(tMiddle, tStop, maxDeltaT, a, htsA, b, htsB, dP, dV, orientationA, ref tCollision);
    }

    /// <summary>The C++ <c>DoGilbert</c> GJK (<c>HitTest.cpp:1858-1948</c>).</summary>
    private static bool DoGilbert(Collider a, int htsA,
        Vector3 positionStart, Vector3 positionStop, Vector3 dV, Orientation orientationHullA,
        Collider b, int htsB, Vector3[] simplex)
    {
        int nVertices = 3;
        const int cMaxHistory = 100;
        var vertexHistory = new Vector3[cMaxHistory];
        vertexHistory[0] = simplex[0];
        vertexHistory[1] = simplex[1];
        vertexHistory[2] = simplex[2];
        vertexHistory[3] = simplex[3];
        int vertexID = 3;

        while (true)
        {
            int[] indices = new int[3];
            int nMin = Johnson(++nVertices, simplex, out var cp, indices);

            if (nMin == 4) return true;             // origin inside tetrahedron

            double l2 = cp.LengthSquared();
            if (l2 < Epsilon * Epsilon) return true;
            cp /= (float)Math.Sqrt(l2);

            Vector3 v0 = a.GetMinExtreme(htsA, (cp * dV >= 0f) ? positionStop : positionStart, orientationHullA, cp)
                       - b.GetMaxExtreme(htsB, cp);

            if ((v0 * cp) > 0f) return false;       // separating plane

            for (int i = vertexID; i >= 0; i--)
                if ((vertexHistory[i] - v0).LengthSquared() < Epsilon * Epsilon)
                    return false;                   // duplicate point → simplex can't contain origin

            vertexID++;
            if (vertexID >= cMaxHistory) return false;
            vertexHistory[vertexID] = v0;

            for (int i = 0; i < nMin; i++)
                if (indices[i] != i) { simplex[i] = simplex[indices[i]]; }
            nVertices = nMin;
            simplex[nVertices] = v0;
        }
    }

    /// <summary>Dispatch — the C++ <c>Johnson</c> (<c>HitTest.cpp:1827-1856</c>).</summary>
    private static int Johnson(int n, Vector3[] p, out Vector3 cp, int[] index)
    {
        switch (n)
        {
            case 1: cp = p[0]; index[0] = 0; return 1;
            case 2: return Johnson2(p, out cp, index);
            case 3: return Johnson3(p, out cp, index);
            default: return Johnson4(p, out cp, index);
        }
    }

    private static int closest_vertex(int v0, Vector3[] p, int[] index, out Vector3 cp)
    {
        index[0] = v0;
        cp = p[v0];
        return 1;
    }

    private static int closest_edge(double e0, double e1, int ie0, int ie1, Vector3[] p, int[] index, out Vector3 cp)
    {
        double esum_inv = 1.0 / (e0 + e1);
        float l0 = (float)(e0 * esum_inv);
        float l1 = (float)(e1 * esum_inv);
        index[0] = ie0; index[1] = ie1;
        cp = p[ie0] * l0 + p[ie1] * l1;
        return 2;
    }

    private static int closest_face(double f0, double f1, double f2, int if0, int if1, int if2,
        Vector3[] p, int[] index, out Vector3 cp)
    {
        double fsum_inv = 1.0 / (f0 + f1 + f2);
        float l0 = (float)(f0 * fsum_inv), l1 = (float)(f1 * fsum_inv), l2 = (float)(f2 * fsum_inv);
        index[0] = if0; index[1] = if1; index[2] = if2;
        cp = p[if0] * l0 + p[if1] * l1 + p[if2] * l2;
        return 3;
    }

    private static int Johnson2(Vector3[] p, out Vector3 cp, int[] index)
    {
        Vector3 d = p[1] - p[0];
        double l0 = p[0] * d;
        if (l0 >= 0) { cp = p[0]; index[0] = 0; return 1; }
        double l1 = p[1] * d;
        if (l1 <= 0) { cp = p[1]; index[0] = 1; return 1; }
        index[0] = 0; index[1] = 1;
        cp = p[0] + (float)(l0 / (l0 - l1)) * d;
        return 2;
    }

    private static int Johnson3(Vector3[] p, out Vector3 cp, int[] index)
    {
        // Mappings of subsets to unique ids, as in the C++ (HitTest.cpp:1316-1330).
        int[][] subset2 =
        [
            [-1, 3, 4],
            [3, -1, 5],
            [4, 5, -1],
        ];
        const int subset3 = 6;

        var dp = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = i; j < 3; j++)
                dp[i, j] = dp[j, i] = (double)p[i].X * p[j].X + (double)p[i].Y * p[j].Y + (double)p[i].Z * p[j].Z;

        var delta = new double[3, subset3 + 1];

        void SetDelta(int i, int j) => delta[i, subset2[j][i]] = dp[j, j] - dp[j, i];
        SetDelta(1, 0); SetDelta(2, 0);
        SetDelta(0, 1); SetDelta(2, 1);
        SetDelta(0, 2); SetDelta(1, 2);

        void SetDelta3(int i, int j, int k) =>
            delta[i, subset3] = delta[j, subset2[j][k]] * (dp[j, j] - dp[j, i]) +
                                delta[k, subset2[j][k]] * (dp[k, j] - dp[k, i]);
        SetDelta3(2, 0, 1); SetDelta3(1, 0, 2); SetDelta3(0, 1, 2);

        // Inside for the triangle. The last clause of the test is degenerate
        // since there is no Pj not in S.
        if (delta[0, subset3] > 0.0 && delta[1, subset3] > 0.0 && delta[2, subset3] > 0.0)
            return closest_face(delta[0, subset3], delta[1, subset3], delta[2, subset3], 0, 1, 2, p, index, out cp);

        // Each vertex (the first clause is degenerate since delta[i][S[i]] = 1).
        if (delta[0, subset2[1][0]] <= 0.0 && delta[0, subset2[2][0]] <= 0.0)
            return closest_vertex(0, p, index, out cp);
        if (delta[1, subset2[0][1]] <= 0.0 && delta[1, subset2[2][1]] <= 0.0)
            return closest_vertex(1, p, index, out cp);
        if (delta[2, subset2[0][2]] <= 0.0 && delta[2, subset2[1][2]] <= 0.0)
            return closest_vertex(2, p, index, out cp);

        // Each edge i,j: delta[i][{i,j}]>0, delta[j][{i,j}]>0, delta[k][subset3]<=0.
        if (delta[0, subset2[0][1]] > 0.0 && delta[1, subset2[0][1]] > 0.0 && delta[2, subset3] <= 0.0)
            return closest_edge(delta[0, subset2[0][1]], delta[1, subset2[0][1]], 0, 1, p, index, out cp);
        if (delta[0, subset2[0][2]] > 0.0 && delta[2, subset2[0][2]] > 0.0 && delta[1, subset3] <= 0.0)
            return closest_edge(delta[0, subset2[0][2]], delta[2, subset2[0][2]], 0, 2, p, index, out cp);
        if (delta[1, subset2[1][2]] > 0.0 && delta[2, subset2[1][2]] > 0.0 && delta[0, subset3] <= 0.0)
            return closest_edge(delta[1, subset2[1][2]], delta[2, subset2[1][2]], 1, 2, p, index, out cp);

        // Fall back to the nearest vertex, then edge, then face (HitTest.cpp:1474-1542).
        int nMin;
        double dMin2 = double.MaxValue;
        int v = 0;
        for (int i = 0; i < 3; i++)
            if (dp[i, i] < dMin2) { dMin2 = dp[i, i]; v = i; }
        nMin = closest_vertex(v, p, index, out cp);

        for (int i = 0; i < 2; i++)
            for (int j = i + 1; j < 3; j++)
                if (delta[i, subset2[i][j]] > 0.0 && delta[j, subset2[i][j]] > 0.0)
                {
                    double d = (delta[i, subset2[i][j]] * dp[i, i] + delta[j, subset2[i][j]] * dp[j, i]) /
                               (delta[i, subset2[i][j]] + delta[j, subset2[i][j]]);
                    if (d < dMin2)
                    {
                        dMin2 = d;
                        nMin = closest_edge(delta[i, subset2[i][j]], delta[j, subset2[i][j]], i, j, p, index, out cp);
                    }
                }

        // Face fallback (HitTest.cpp:1518-1539): the whole triangle.
        if (delta[0, subset3] > 0.0 && delta[1, subset3] > 0.0 && delta[2, subset3] > 0.0)
        {
            double d = (delta[0, subset3] * dp[0, 0] + delta[1, subset3] * dp[1, 0] + delta[2, subset3] * dp[2, 0]) /
                       (delta[0, subset3] + delta[1, subset3] + delta[2, subset3]);
            if (d < dMin2)
            {
                dMin2 = d;
                nMin = closest_face(delta[0, subset3], delta[1, subset3], delta[2, subset3], 0, 1, 2, p, index, out cp);
            }
        }
        return nMin;
    }

    private static int Johnson4(Vector3[] p, out Vector3 cp, int[] index)
    {
        int[][] subset2 =
        [
            [-1, 4, 5, 6],
            [4, -1, 7, 8],
            [5, 7, -1, 9],
            [6, 8, 9, -1],
        ];
        int[] subset3 = [10, 11, 12, 13];
        const int subset4 = 14;

        var dp = new double[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = i; j < 4; j++)
                dp[i, j] = dp[j, i] = (double)p[i].X * p[j].X + (double)p[i].Y * p[j].Y + (double)p[i].Z * p[j].Z;

        var delta = new double[4, subset4 + 1];

        void SetDelta(int i, int j) => delta[i, subset2[j][i]] = dp[j, j] - dp[j, i];
        SetDelta(1, 0); SetDelta(2, 0); SetDelta(3, 0);
        SetDelta(0, 1); SetDelta(2, 1); SetDelta(3, 1);
        SetDelta(0, 2); SetDelta(1, 2); SetDelta(3, 2);
        SetDelta(0, 3); SetDelta(1, 3); SetDelta(2, 3);

        void SetDelta3(int i, int j, int k, int x) =>
            delta[i, subset3[x]] = delta[j, subset2[j][k]] * (dp[j, j] - dp[j, i]) +
                                   delta[k, subset2[j][k]] * (dp[k, j] - dp[k, i]);
        SetDelta3(2, 0, 1, 3); SetDelta3(3, 0, 1, 2);
        SetDelta3(1, 0, 2, 3); SetDelta3(3, 0, 2, 1);
        SetDelta3(1, 0, 3, 2); SetDelta3(2, 0, 3, 1);
        SetDelta3(0, 1, 2, 3); SetDelta3(3, 1, 2, 0);
        SetDelta3(0, 1, 3, 2); SetDelta3(2, 1, 3, 0);
        SetDelta3(0, 2, 3, 1); SetDelta3(1, 2, 3, 0);

        void SetDelta4(int i, int j, int k, int l) =>
            delta[i, subset4] = delta[j, subset3[i]] * (dp[j, j] - dp[j, i]) +
                                delta[k, subset3[i]] * (dp[k, j] - dp[k, i]) +
                                delta[l, subset3[i]] * (dp[l, j] - dp[l, i]);
        SetDelta4(0, 1, 2, 3); SetDelta4(1, 0, 2, 3);
        SetDelta4(2, 0, 1, 3); SetDelta4(3, 0, 1, 2);

        // Inside for the tetrahedron (all deltas on subset4 positive).
        if (delta[0, subset4] > 0.0 && delta[1, subset4] > 0.0 && delta[2, subset4] > 0.0 && delta[3, subset4] > 0.0)
        { cp = Vector3.Zero; return 4; }

        // Each vertex.
        if (delta[0, subset2[1][0]] <= 0.0 && delta[0, subset2[2][0]] <= 0.0 && delta[0, subset2[3][0]] <= 0.0)
            return closest_vertex(0, p, index, out cp);
        if (delta[1, subset2[0][1]] <= 0.0 && delta[1, subset2[2][1]] <= 0.0 && delta[1, subset2[3][1]] <= 0.0)
            return closest_vertex(1, p, index, out cp);
        if (delta[2, subset2[0][2]] <= 0.0 && delta[2, subset2[1][2]] <= 0.0 && delta[2, subset2[3][2]] <= 0.0)
            return closest_vertex(2, p, index, out cp);
        if (delta[3, subset2[0][3]] <= 0.0 && delta[3, subset2[1][3]] <= 0.0 && delta[3, subset2[2][3]] <= 0.0)
            return closest_vertex(3, p, index, out cp);

        // Each edge i,j.
        if (delta[0, subset2[0][1]] > 0.0 && delta[1, subset2[0][1]] > 0.0 &&
            delta[2, subset3[3]] <= 0.0 && delta[3, subset3[2]] <= 0.0)
            return closest_edge(delta[0, subset2[0][1]], delta[1, subset2[0][1]], 0, 1, p, index, out cp);
        if (delta[0, subset2[0][2]] > 0.0 && delta[2, subset2[0][2]] > 0.0 &&
            delta[1, subset3[3]] <= 0.0 && delta[3, subset3[1]] <= 0.0)
            return closest_edge(delta[0, subset2[0][2]], delta[2, subset2[0][2]], 0, 2, p, index, out cp);
        if (delta[0, subset2[0][3]] > 0.0 && delta[3, subset2[0][3]] > 0.0 &&
            delta[1, subset3[2]] <= 0.0 && delta[2, subset3[1]] <= 0.0)
            return closest_edge(delta[0, subset2[0][3]], delta[3, subset2[0][3]], 0, 3, p, index, out cp);
        if (delta[1, subset2[1][2]] > 0.0 && delta[2, subset2[1][2]] > 0.0 &&
            delta[0, subset3[3]] <= 0.0 && delta[3, subset3[0]] <= 0.0)
            return closest_edge(delta[1, subset2[1][2]], delta[2, subset2[1][2]], 1, 2, p, index, out cp);
        if (delta[1, subset2[1][3]] > 0.0 && delta[3, subset2[1][3]] > 0.0 &&
            delta[0, subset3[2]] <= 0.0 && delta[2, subset3[0]] <= 0.0)
            return closest_edge(delta[1, subset2[1][3]], delta[3, subset2[1][3]], 1, 3, p, index, out cp);
        if (delta[2, subset2[2][3]] > 0.0 && delta[3, subset2[2][3]] > 0.0 &&
            delta[0, subset3[1]] <= 0.0 && delta[1, subset3[0]] <= 0.0)
            return closest_edge(delta[2, subset2[2][3]], delta[3, subset2[2][3]], 2, 3, p, index, out cp);

        // Each face i,j,k.
        if (delta[0, subset3[3]] > 0.0 && delta[1, subset3[3]] > 0.0 && delta[2, subset3[3]] > 0.0 && delta[3, subset4] <= 0.0)
            return closest_face(delta[0, subset3[3]], delta[1, subset3[3]], delta[2, subset3[3]], 0, 1, 2, p, index, out cp);
        if (delta[0, subset3[2]] > 0.0 && delta[1, subset3[2]] > 0.0 && delta[3, subset3[2]] > 0.0 && delta[2, subset4] <= 0.0)
            return closest_face(delta[0, subset3[2]], delta[1, subset3[2]], delta[3, subset3[2]], 0, 1, 3, p, index, out cp);
        if (delta[0, subset3[1]] > 0.0 && delta[2, subset3[1]] > 0.0 && delta[3, subset3[1]] > 0.0 && delta[1, subset4] <= 0.0)
            return closest_face(delta[0, subset3[1]], delta[2, subset3[1]], delta[3, subset3[1]], 0, 2, 3, p, index, out cp);
        if (delta[1, subset3[0]] > 0.0 && delta[2, subset3[0]] > 0.0 && delta[3, subset3[0]] > 0.0 && delta[0, subset4] <= 0.0)
            return closest_face(delta[1, subset3[0]], delta[2, subset3[0]], delta[3, subset3[0]], 1, 2, 3, p, index, out cp);

        // Fall back to the nearest vertex, then edge, then face (HitTest.cpp:1744-1825).
        int nMin;
        double dMin2 = double.MaxValue;
        int v = 0;
        for (int i = 0; i < 4; i++)
            if (dp[i, i] < dMin2) { dMin2 = dp[i, i]; v = i; }
        nMin = closest_vertex(v, p, index, out cp);

        for (int i = 0; i < 3; i++)
            for (int j = i + 1; j < 4; j++)
                if (delta[i, subset2[i][j]] > 0.0 && delta[j, subset2[i][j]] > 0.0)
                {
                    double d = (delta[i, subset2[i][j]] * dp[i, i] + delta[j, subset2[i][j]] * dp[j, i]) /
                               (delta[i, subset2[i][j]] + delta[j, subset2[i][j]]);
                    if (d < dMin2)
                    {
                        dMin2 = d;
                        nMin = closest_edge(delta[i, subset2[i][j]], delta[j, subset2[i][j]], i, j, p, index, out cp);
                    }
                }

        var idsFace = new[,] { { 0, 1, 2, 3 }, { 0, 1, 3, 2 }, { 0, 2, 3, 1 }, { 1, 2, 3, 0 } };
        for (int f = 0; f < 4; f++)
        {
            int i = idsFace[f, 0], j = idsFace[f, 1], k = idsFace[f, 2], l = idsFace[f, 3];
            if (delta[i, subset3[l]] > 0.0 && delta[j, subset3[l]] > 0.0 && delta[k, subset3[l]] > 0.0)
            {
                double d = (delta[i, subset3[l]] * dp[i, i] + delta[j, subset3[l]] * dp[j, i] +
                            delta[k, subset3[l]] * dp[k, i]) /
                           (delta[i, subset3[l]] + delta[j, subset3[l]] + delta[k, subset3[l]]);
                if (d < dMin2)
                {
                    dMin2 = d;
                    nMin = closest_face(delta[i, subset3[l]], delta[j, subset3[l]], delta[k, subset3[l]],
                                        i, j, k, p, index, out cp);
                }
            }
        }
        return nMin;
    }

    /// <summary>Compose two orientations: result = <c>a · b⁻¹</c> (<c>oA.TimesInverse(oB)</c>), the orientation of A in B's frame, used by <c>HullCollide</c>.</summary>
    private static Orientation ComposeInverse(Orientation a, Orientation b)
    {
        var o = new Orientation();
        o.SetRight(b.TimesInverse(a.GetRight()));
        o.SetUp(b.TimesInverse(a.GetUp()));
        // backward row = b.TimesInverse(a.GetBackward()); SetForward negates.
        o.SetForward(-b.TimesInverse(a.GetBackward()));
        return o;
    }
}
