namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>P828 — how one radar contact's native label block is drawn this
/// frame once the Modern-HUD declutter option has run.</summary>
public enum RadarLabelDraw : byte
{
    /// <summary>The contact keeps its native icon/bracket but gets no label
    /// block (no name, no range line, no stat bars).</summary>
    IconOnly = 0,
    /// <summary>The full native label block draws (name/range/bars exactly as
    /// <see cref="RadarLabelModel"/> resolved them, faded by
    /// <see cref="RadarLabelDeclutter.Alpha"/>).</summary>
    Label = 1,
    /// <summary>The label is dropped and folded into a
    /// <see cref="RadarLabelMergeTag"/> count tag next to its cluster.</summary>
    Merged = 2,
}

/// <summary>P828 — the Modern-HUD declutter knobs. Pure values (no MonoGame
/// types) so the selection is unit-testable and the renderer only consumes the
/// decisions.
/// <para><b>Off</b> is the zero-value-safe native behaviour: every label the
/// radar mode allows is drawn, at full opacity, with no clustering — the exact
/// pre-P828 draw loop. Classic always uses it (native has no declutter), Modern
/// defaults to <see cref="Modern"/>.</para></summary>
public readonly record struct RadarLabelDeclutterOptions(
    bool Enabled,
    int MaxExtraLabels,
    float FadeNear,
    float FadeFar,
    float MinAlpha,
    float MaxAlpha,
    float MergeCellWidth,
    float MergeCellHeight,
    int MergeMinCount,
    bool Telemetry,
    // P836 — the Modern HUD-level label-policy knobs. Kept with defaults so the
    // pre-P836 factories (Off / Modern) and tests compile and behave unchanged.
    bool AllAllowed = false,
    bool LabelFriendlies = true,
    bool OnlyTarget = false,
    OffscreenRule Offscreen = OffscreenRule.NativeBlock)
{
    /// <summary>Native draw order, untouched: the option is inert.</summary>
    public static readonly RadarLabelDeclutterOptions Off =
        new(false, 0, 0f, 0f, 1f, 1f, 0f, 0f, int.MaxValue, false);

    /// <summary>The Modern-HUD plan: label the priority contacts plus
    /// <paramref name="maxExtraLabels"/> nearest other allowed contacts, fade
    /// them with distance, and fold crowded labels into count tags.
    /// <paramref name="scale"/> is the HUD text scale so the merge grid follows
    /// the labels' own size.</summary>
    public static RadarLabelDeclutterOptions Modern(int maxExtraLabels, float scale, bool telemetry = false)
        => new(
            Enabled: true,
            MaxExtraLabels: Math.Clamp(maxExtraLabels, 0, RadarLabelDeclutter.MaxExtraLabelsLimit),
            FadeNear: RadarLabelDeclutter.DefaultFadeNear,
            FadeFar: RadarLabelDeclutter.DefaultFadeFar,
            MinAlpha: RadarLabelDeclutter.DefaultMinAlpha,
            MaxAlpha: RadarLabelDeclutter.DefaultMaxAlpha,
            MergeCellWidth: RadarLabelDeclutter.DefaultMergeCellWidth * GridScale(scale),
            MergeCellHeight: RadarLabelDeclutter.DefaultMergeCellHeight * GridScale(scale),
            MergeMinCount: 2,
            Telemetry: telemetry);

    private static float GridScale(float scale) => float.IsFinite(scale) && scale > 0f ? scale : 1f;

    /// <summary>P836 — build the renderer options from a HUD-level preset.
    /// <paramref name="extraLabels"/> is the effective nearest-N (the level's
    /// suggestion, overridden by the player's persisted <c>RadarLabelMax</c> for
    /// a capped level); ignored by Full/Cinematic which do not use the cap.</summary>
    public static RadarLabelDeclutterOptions FromPreset(in HudLevelPreset preset, float scale, int extraLabels, bool telemetry = false)
        => new(
            Enabled: true,
            MaxExtraLabels: preset.AllAllowed || preset.OnlyTarget
                ? 0 : Math.Clamp(extraLabels, 0, RadarLabelDeclutter.MaxExtraLabelsLimit),
            FadeNear: preset.FadeNear,
            FadeFar: preset.FadeFar,
            MinAlpha: RadarLabelDeclutter.DefaultMinAlpha,
            MaxAlpha: RadarLabelDeclutter.DefaultMaxAlpha,
            MergeCellWidth: RadarLabelDeclutter.DefaultMergeCellWidth * GridScale(scale),
            MergeCellHeight: RadarLabelDeclutter.DefaultMergeCellHeight * GridScale(scale),
            MergeMinCount: preset.Merge ? 2 : int.MaxValue,
            Telemetry: telemetry,
            AllAllowed: preset.AllAllowed,
            LabelFriendlies: preset.LabelFriendlies,
            OnlyTarget: preset.OnlyTarget,
            Offscreen: preset.Offscreen);

}

/// <summary>P828 — one compact "N labels collapsed here" tag. <see cref="Point"/>
/// is the cluster centroid in screen pixels, <see cref="Enemy"/> keeps an enemy
/// cluster apart from a friendly one in the same grid cell (two tags, native
/// per-side colours), and <see cref="Alpha"/> is the members' mean distance
/// fade.</summary>
public readonly record struct RadarLabelMergeTag(
    HudPoint Point, int Count, bool Enemy, float Alpha, HudColor? SideColor);

/// <summary>
/// P828 — the Modern-HUD radar label declutter: a pure, allocation-free-per-frame
/// selection over the frame's <see cref="HudContact"/> list that decides
/// <em>how many of the labels the radar mode already allows</em> are drawn and
/// how. It never decides <em>whether</em> a label is allowed — that stays the
/// native per-kind tier switch (<see cref="RadarLabelModel.Spec"/>, P783) and the
/// P624 radar mode; a contact native suppresses can never be selected here
/// because the selector asks <see cref="RadarLabelModel.Resolve"/> first.
///
/// <para><b>Why it exists.</b> Native's Default radar labels every on-screen ship
/// (<c>WinTrek/radarimage.cpp:747-756</c> — friendlies name-only, enemies name +
/// stats + range), which is right for a 20-60 player game and a wall of text at
/// the 200v200 showcase (~390 ships in Alderan). The declutter is a Modern-HUD
/// visual option (Classic stays exactly native), default on in Modern.</para>
///
/// <para><b>Selection law.</b>
/// <list type="number">
/// <item><b>Priority contacts are always labelled</b> (never merged): the
/// native important mask (<see cref="RadarLabelModel.IsImportant"/> — your
/// selected target/missile-lock target, your accepted/queued orders, the
/// contacts damaging you, the construction highlight, the carried
/// flag/artifact), plus your chat/command subjects (wingmen and command
/// recipients, <see cref="HudContact.Subject"/>) and your own ship when native
/// labels it.</item>
/// <item><b>The nearest N other allowed contacts</b> are labelled too: enemy
/// ships first, then friendly ships, then every other contact kind (stations,
/// warps, asteroids, treasures, probes/mines/missiles — mostly bare range
/// lines, which at 1000+ contacts are the bulk of the clutter), nearest first
/// within each group, ties keeping contact order. Everything else is icon/
/// bracket only.</item>
/// <item><b>Distance fade</b>: label alpha is full within
/// <see cref="RadarLabelDeclutterOptions.FadeNear"/> of the camera, falling
/// linearly to <see cref="RadarLabelDeclutterOptions.MinAlpha"/> at
/// <see cref="RadarLabelDeclutterOptions.FadeFar"/> and clamped there.</item>
/// <item><b>Overlap merge</b>: the ordinary (non-priority) labels are bucketed
/// into a deterministic screen-space grid; a bucket holding at least
/// <see cref="RadarLabelDeclutterOptions.MergeMinCount"/> labels on one side
/// collapses into a single count tag. Deterministic: buckets are keyed by
/// integer cell + side and scanned in contact order, so the same frame always
/// produces the same tags.</item>
/// </list></para>
///
/// <para><b>Budget.</b> One instance is owned by the HUD renderer and reused
/// every frame: its decision/scratch arrays grow once, the cluster and tag lists
/// are cleared in place, and the count-tag strings come from a metre-style cache
/// (<see cref="CountTag"/>) — <see cref="Select"/> allocates nothing once warmed
/// (pinned by <c>P828RadarLabelDeclutterTests</c>).</para>
/// </summary>
public sealed class RadarLabelDeclutter
{
    /// <summary>The default number of ordinary (non-priority) labels per frame
    /// ("nearest N", the brief's ~12).</summary>
    public const int DefaultExtraLabels = 12;
    /// <summary>Upper bound for the setting so a mistyped profile cannot ask for
    /// thousands of labels.</summary>
    public const int MaxExtraLabelsLimit = 64;
    /// <summary>Native's own distance fade, written but commented out in
    /// <c>WinTrek/radarimage.cpp:686-700</c> (<c>closeRange = 100</c>,
    /// <c>farRange = 1000</c>, <c>closeAlpha = 0.75f</c>, <c>farAlpha = 0.25f</c>,
    /// linear between, applied to the contact colour). The declutter keeps its
    /// shape and alphas; the distances are tunable per HUD level (the native pair
    /// suits small games; a 200v200 sector is mostly beyond 1 km).</summary>
    public const float NativeFadeNear = 100f, NativeFadeFar = 1000f;
    /// <inheritdoc cref="NativeFadeNear"/>
    public const float NativeNearAlpha = .75f, NativeFarAlpha = .25f;
    /// <summary>Modern declutter: native alpha at/below this camera distance (metres).</summary>
    public const float DefaultFadeNear = 900f;
    /// <summary>Distance at which a label reaches <see cref="DefaultMinAlpha"/>.</summary>
    public const float DefaultFadeFar = 4500f;
    /// <summary>Faintest a far label gets: native <c>farAlpha</c>.</summary>
    public const float DefaultMinAlpha = NativeFarAlpha;
    /// <summary>Nearest labels: native <c>closeAlpha</c>.</summary>
    public const float DefaultMaxAlpha = NativeNearAlpha;
    /// <summary>Merge grid cell, in pixels at HUD scale 1 (a label block is
    /// roughly 100x30 px there).</summary>
    public const float DefaultMergeCellWidth = 120f;
    public const float DefaultMergeCellHeight = 46f;

    private struct Cluster
    {
        public int CellX, CellY, Count;
        public bool Enemy;
        public float SumX, SumY, SumAlpha;
        public HudColor? SideColor;
    }

    private byte[] _draw = Array.Empty<byte>();
    private float[] _alpha = Array.Empty<float>();
    private int[] _clusterOf = Array.Empty<int>();
    private float[] _distance = Array.Empty<float>();
    private readonly int[] _best = new int[MaxExtraLabelsLimit];
    private readonly float[] _bestDistance = new float[MaxExtraLabelsLimit];
    private readonly byte[] _bestRank = new byte[MaxExtraLabelsLimit];
    private int _bestCount;
    private readonly List<Cluster> _clusters = new();
    private readonly List<RadarLabelMergeTag> _tags = new();

    /// <summary>Contacts examined in the last <see cref="Select"/> (telemetry).</summary>
    public int ContactCount { get; private set; }
    /// <summary>Contacts whose full label block draws (telemetry).</summary>
    public int LabelledCount { get; private set; }
    /// <summary>Contacts folded into count tags (telemetry).</summary>
    public int MergedCount { get; private set; }
    /// <summary>Count tags produced (telemetry).</summary>
    public int TagCount => _tags.Count;

    /// <summary>The count tags for the frame (empty when the option is off or
    /// nothing clustered). Rebuilt in place each frame.</summary>
    public IReadOnlyList<RadarLabelMergeTag> Tags => _tags;

    /// <summary>The per-contact decision the renderer consumes.</summary>
    public RadarLabelDraw Draw(int index)
        => index >= 0 && index < _draw.Length ? (RadarLabelDraw)_draw[index] : RadarLabelDraw.IconOnly;

    /// <summary>True when the contact's full label block draws.</summary>
    public bool IsLabelled(int index) => Draw(index) == RadarLabelDraw.Label;

    /// <summary>The label-block alpha multiplier (1 when the option is off).</summary>
    public float Alpha(int index) => index >= 0 && index < _alpha.Length ? _alpha[index] : 1f;

    /// <summary>Whether a contact is exempt from the N cap and from merging.
    /// Pure and native-cited so the priority set is unit-pinnable.
    /// <para>The native important mask is
    /// <c>radarimage.cpp:704-710</c> (<c>c_maskTarget | c_maskAccepted |
    /// c_maskQueued | c_maskHighlight | c_maskThreat | c_maskFlag |
    /// c_maskArtifact</c>) — it already forces a label in every LOD. This port
    /// adds the chat/command subject (<c>c_maskSubject</c>, :652 — wingmen and
    /// command recipients, the brief's "wingmen/command recipients") and the own
    /// ship. There is no incoming-missile-lock mask in native
    /// (<c>radarimage.cpp:30-42</c>) or on the port's wire, so "locked by" is
    /// represented by the native threat bucket (the ships that actually damaged
    /// you in the 40 s window, <c>c_maskThreat</c>, :660-671) and "locked on" by
    /// the selected target (<c>c_maskTarget</c>, the same object the missile lock
    /// follows).</para></summary>
    public static bool IsAlwaysLabelled(in HudContact contact)
        => contact.IsSelf || contact.Subject || RadarLabelModel.IsImportant(contact);

    /// <summary>The distance a label's fade and the nearest-N order use: the
    /// camera distance the radar snapshot recorded, else the native range line
    /// value, else 0 (headless fixtures). Non-finite values are treated as very
    /// far so a bad reading cannot win the N cap.</summary>
    public static float Distance(in HudContact contact)
    {
        float distance = contact.CameraDistance ?? contact.Range ?? 0f;
        return float.IsFinite(distance) && distance >= 0f ? distance : float.MaxValue;
    }

    /// <summary>The distance fade (native shape, <c>radarimage.cpp:686-700</c>):
    /// <paramref name="maxAlpha"/> at/below <paramref name="fadeNear"/>, linear down
    /// to <paramref name="minAlpha"/> at <paramref name="fadeFar"/>, clamped outside.
    /// Monotonically non-increasing in distance.</summary>
    public static float Fade(float distance, float fadeNear, float fadeFar, float minAlpha, float maxAlpha = 1f)
    {
        float ceiling = Math.Clamp(maxAlpha, 0f, 1f);
        float floor = Math.Min(Math.Clamp(minAlpha, 0f, 1f), ceiling);
        if (!float.IsFinite(distance)) return floor;
        if (!(fadeFar > fadeNear)) return distance <= fadeNear ? ceiling : floor;
        float t = Math.Clamp((distance - fadeNear) / (fadeFar - fadeNear), 0f, 1f);
        return ceiling + t * (floor - ceiling);
    }

    /// <summary>Fade for one contact under <paramref name="options"/>.</summary>
    public static float Fade(in HudContact contact, in RadarLabelDeclutterOptions options)
        => Fade(Distance(contact), options.FadeNear, options.FadeFar, options.MinAlpha, options.MaxAlpha);

    /// <summary>The compact count tag text. The bundled native HUD fonts carry
    /// ASCII 32-126 only (<c>Content/Fonts/NativeR8.spritefont</c>), so the tag
    /// uses <c>x37</c> rather than the typographic <c>×37</c>. Cached by count so
    /// a steady frame allocates no string.</summary>
    public static string CountTag(int count)
    {
        if (count <= 0) return "x0";
        if (_countTags.TryGetValue(count, out var cached)) return cached;
        var text = "x" + count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_countTags.Count >= CountTagCacheLimit) _countTags.Clear();
        _countTags[count] = text;
        return text;
    }

    private const int CountTagCacheLimit = 512;
    private static readonly Dictionary<int, string> _countTags = new();

    /// <summary>Run the selection for one frame. <paramref name="cache"/> is the
    /// renderer's per-contact label cache; when supplied the "does native allow
    /// a label here" test reuses its already-resolved slot instead of resolving
    /// twice (still allocation-free).</summary>
    public void Select(IReadOnlyList<HudContact> contacts, RadarMode mode, bool commandCamera,
        in RadarLabelDeclutterOptions options, RadarLabelCache? cache = null)
    {
        int n = contacts.Count;
        EnsureCapacity(n);
        _clusters.Clear();
        _tags.Clear();
        _bestCount = 0;
        ContactCount = n;
        LabelledCount = 0;
        MergedCount = 0;

        for (int i = 0; i < n; i++)
        {
            _draw[i] = (byte)RadarLabelDraw.IconOnly;
            _alpha[i] = 1f;
            _clusterOf[i] = -1;
        }

        if (!options.Enabled)
        {
            // Native path: every label the radar mode allows, full opacity.
            for (int i = 0; i < n; i++)
            {
                if (!HasLabel(contacts[i], mode, commandCamera, i, cache)) continue;
                _draw[i] = (byte)RadarLabelDraw.Label;
                LabelledCount++;
            }
            return;
        }

        int cap = Math.Clamp(options.MaxExtraLabels, 0, MaxExtraLabelsLimit);
        float cellWidth = Cell(options.MergeCellWidth, DefaultMergeCellWidth);
        float cellHeight = Cell(options.MergeCellHeight, DefaultMergeCellHeight);
        int clusterMin = Math.Max(2, options.MergeMinCount);

        // Pass 1 — per-contact draw decision. The priority set (target,
        // accepted/queued order, threat, subjects/wingmen, own ship) always
        // draws; the rest compete for the N nearest slots unless the level's
        // policy overrides (Full labels everything, Combat drops friendlies,
        // Cinematic keeps only the target, and an off-screen contact follows
        // the level's off-screen rule).
        for (int i = 0; i < n; i++)
        {
            var contact = contacts[i];
            if (!HasLabel(contact, mode, commandCamera, i, cache)) continue;
            float distance = Distance(contact);
            _distance[i] = distance;
            // Cinematic — no contact label except the selected target.
            if (options.OnlyTarget)
            {
                if (contact.Selected)
                {
                    _draw[i] = (byte)RadarLabelDraw.Label;
                    _alpha[i] = Fade(distance, options.FadeNear, options.FadeFar, options.MinAlpha);
                }
                continue;
            }
            bool priority = IsAlwaysLabelled(contact);
            // P836 — an OFF-SCREEN ordinary contact obeys the level's off-screen
            // rule: EdgeDot suppresses its label (the bracket renderer draws a
            // pip instead) and None drops it entirely. Priority contacts always
            // keep their native block on the edge.
            if (!priority && !contact.OnScreen && options.Offscreen != OffscreenRule.NativeBlock)
                continue;   // stays IconOnly
            if (priority)
            {
                _draw[i] = (byte)RadarLabelDraw.Label;
                _alpha[i] = Fade(distance, options.FadeNear, options.FadeFar, options.MinAlpha);
                continue;
            }
            // Full — every native-allowed label draws (faded, no cap, no merge).
            if (options.AllAllowed)
            {
                _draw[i] = (byte)RadarLabelDraw.Label;
                _alpha[i] = Fade(distance, options.FadeNear, options.FadeFar, options.MinAlpha);
                continue;
            }
            // Combat — friendlies are icon-only and never eat the cap.
            if (!options.LabelFriendlies && contact.Kind == HudContactKind.Ship && !contact.IsEnemy)
                continue;
            byte rank = (byte)Rank(contact);
            Offer(i, rank, distance, cap);
        }
        for (int b = 0; b < _bestCount; b++)
        {
            int i = _best[b];
            _draw[i] = (byte)RadarLabelDraw.Label;
            _alpha[i] = Fade(_distance[i], options.FadeNear, options.FadeFar, options.MinAlpha);
        }

        // Pass 2 — bucket the ordinary labels into the deterministic screen grid.
        for (int b = 0; b < _bestCount; b++)
        {
            int i = _best[b];
            var contact = contacts[i];
            int cellX = (int)MathF.Floor(contact.Position.X / cellWidth);
            int cellY = (int)MathF.Floor(contact.Position.Y / cellHeight);
            int found = -1;
            for (int c = 0; c < _clusters.Count; c++)
            {
                var candidate = _clusters[c];
                if (candidate.CellX == cellX && candidate.CellY == cellY && candidate.Enemy == contact.IsEnemy)
                {
                    found = c;
                    break;
                }
            }
            if (found < 0)
            {
                found = _clusters.Count;
                _clusters.Add(new Cluster { CellX = cellX, CellY = cellY, Enemy = contact.IsEnemy, SideColor = contact.SideColor });
            }
            var cluster = _clusters[found];
            cluster.Count++;
            cluster.SumX += contact.Position.X;
            cluster.SumY += contact.Position.Y;
            cluster.SumAlpha += _alpha[i];
            _clusters[found] = cluster;
            _clusterOf[i] = found;
        }

        // A bucket with enough members collapses into one count tag; a lone label
        // keeps its block. Two tags (enemy + friendly) can share one cell.
        for (int c = 0; c < _clusters.Count; c++)
        {
            var cluster = _clusters[c];
            if (cluster.Count < clusterMin) continue;
            _tags.Add(new RadarLabelMergeTag(
                new HudPoint(cluster.SumX / cluster.Count, cluster.SumY / cluster.Count),
                cluster.Count, cluster.Enemy, cluster.SumAlpha / cluster.Count, cluster.SideColor));
        }
        for (int i = 0; i < n; i++)
        {
            int c = _clusterOf[i];
            if (c >= 0 && _clusters[c].Count >= clusterMin)
                _draw[i] = (byte)RadarLabelDraw.Merged;
            if (_draw[i] == (byte)RadarLabelDraw.Label) LabelledCount++;
            else if (_draw[i] == (byte)RadarLabelDraw.Merged) MergedCount++;
        }
    }

    /// <summary>The N-cap order group for a contact: 0 enemy ship, 1 friendly
    /// ship, 2 every other contact kind. Ships outrank rocks/treasure range
    /// lines so the handful of labels a crowded frame keeps are the ones the
    /// pilot reads — the brief's "enemies before friendlies" extended to "ships
    /// before everything else".</summary>
    public static int Rank(in HudContact contact)
        => contact.Kind != HudContactKind.Ship ? 2 : contact.IsEnemy ? 0 : 1;

    /// <summary>Insert a non-priority contact into the bounded nearest-N list.</summary>
    private void Offer(int index, byte rank, float distance, int cap)
    {
        if (cap <= 0) return;
        int position = _bestCount < cap ? _bestCount : cap;
        while (position > 0
               && Precedes(rank, distance, index,
                   _bestRank[position - 1], _bestDistance[position - 1], _best[position - 1]))
            position--;
        if (position >= cap) return;
        int last = Math.Min(_bestCount, cap - 1);
        for (int k = last; k > position; k--)
        {
            _best[k] = _best[k - 1];
            _bestDistance[k] = _bestDistance[k - 1];
            _bestRank[k] = _bestRank[k - 1];
        }
        _best[position] = index;
        _bestDistance[position] = distance;
        _bestRank[position] = rank;
        if (_bestCount < cap) _bestCount++;
    }

    /// <summary>Ships before other kinds, enemies before friendlies, then nearest
    /// first, then contact order — the deterministic N-cap order.</summary>
    private static bool Precedes(byte rank, float distance, int index,
        byte otherRank, float otherDistance, int otherIndex)
    {
        if (rank != otherRank) return rank < otherRank;
        if (distance != otherDistance) return distance < otherDistance;
        return index < otherIndex;
    }

    private static bool HasLabel(in HudContact contact, RadarMode mode, bool commandCamera, int index,
        RadarLabelCache? cache)
        => cache is { } labels
            ? labels.Resolve(index, contact, mode, commandCamera).Label is not null
            : RadarLabelModel.Resolve(contact, mode, commandCamera) is not null;

    private static float Cell(float value, float fallback)
        => float.IsFinite(value) && value > 0f ? value : fallback;

    private void EnsureCapacity(int count)
    {
        if (count <= _draw.Length) return;
        int size = Math.Max(count, Math.Max(16, _draw.Length * 2));
        Array.Resize(ref _draw, size);
        Array.Resize(ref _alpha, size);
        Array.Resize(ref _clusterOf, size);
        Array.Resize(ref _distance, size);
    }
}
