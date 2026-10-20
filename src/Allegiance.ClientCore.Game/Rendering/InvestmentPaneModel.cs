using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core.Model;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P487 — the pure interaction model of the docked <b>investment</b> pane
/// (native <c>PurchasesPaneImpl</c>, <c>WinTrek/loadout.cpp:578+</c>), where a
/// human player funds their team's construction buckets (technology developments,
/// drone types, station types). Kept MonoGame-free and headless-testable exactly
/// like <see cref="ShopModel"/> (the ScoreboardLayout discipline): it holds the
/// side's synthesized bucket catalog, the live per-bucket funding from the wire,
/// the player's wallet, and the <c>CanInvest</c> gate, and exposes the Invest /
/// partial-invest amounts native computes — the host (<c>Game1.Panes.cs</c>)
/// turns those into real <c>BUCKET_DONATE(64)</c> messages.
///
/// <para><b>Who owns the money.</b> Investing here does NOT touch the wallet.
/// The pane computes an amount and the host sends it via
/// <see cref="Session.ConnectionSession.SendBucketDonate"/>; the server debits the
/// donor's wallet and broadcasts the authoritative <c>MONEY_CHANGE(60)</c> (P442),
/// which is what moves the client's <see cref="ClientWorld.MyMoney"/>. The pane
/// therefore never locally subtracts, so there is no double-spend.</para>
///
/// <para><b>Bucket ids vs the server.</b> The bucket definitions come from
/// <see cref="BucketCatalog.Describe"/>, which reproduces the server's exact id
/// numbering (same core + same initial techs), and funding is correlated by id
/// from <see cref="ClientWorld.BucketFundingFor"/>. Completion is derived, not
/// stored: a bucket is complete once <c>money ≥ price ∧ timeMs ≥ ttb·1000</c>,
/// exactly <c>bucketigc.h:51</c>.</para>
///
/// <para><b>Native <c>CanInvest</c> fidelity</b> (loadout.cpp:1095-1142): this
/// implements mission-in-progress, has-a-side, the side CanBuy gate, the
/// drone-type cap (<c>CountDronesOfType ≥ nMaxDronesPerTeam</c>), the station-type
/// build-spot / no-builder gate (fed in by the host, default open), money &gt; 0,
/// and not-already-100%. The host supplies live CanBuy and predecessor gates (P728).</para>
/// </summary>
public sealed record InvestmentBucket(
    short Id,
    BucketItemType ItemType,
    string Name,
    int Price,
    uint TimeToBuildSec,
    short HullIdDrone,
    short DroneTypeId,
    short DevelopmentId,
    short StationTypeId,
    string Description = "",
    int GroupId = 0);

/// <summary>P487 — the pure interaction model backing the investment pane. See
/// <see cref="InvestmentBucket"/> / the class-level doc for the money-ownership
/// and CanInvest contracts.</summary>
public sealed class InvestmentPaneModel
{
    private static readonly IReadOnlyDictionary<short, int> EmptyDroneCounts = new Dictionary<short, int>();

    private readonly IReadOnlyList<InvestmentBucket> _defs;
    private readonly Dictionary<short, long> _moneyById = new();
    private readonly Dictionary<short, long> _timeMsById = new();

    private readonly bool _missionInProgress;
    private readonly bool _hasSide;
    private readonly int? _myMoney;
    private readonly int _maxDronesPerTeam;
    private readonly IReadOnlyDictionary<short, int> _droneCountByHullId;
    private readonly Func<short, bool> _stationCanBuild;
    private readonly Func<InvestmentBucket, bool> _canBuy;
    private readonly IReadOnlyDictionary<short, short> _predecessors;
    private readonly IReadOnlyDictionary<short, int>? _droneCountByTypeId;

    /// <summary>Show-all toggle (native Show-All / Hide-Completed checkboxes).</summary>
    public bool ShowAll { get; private set; }

    /// <summary>Native Show Complete starts checked (loadout.cpp:763), so
    /// Hide Completed starts off.</summary>
    public bool HideCompleted { get; private set; }

    /// <summary>Cursor index into <see cref="Items"/> (the filtered item rows).</summary>
    public int Cursor { get; private set; }
    public int? SelectedGroup { get; private set; }
    public static readonly string[] GroupNames = ["STARBASE", "GARRISON", "SUPREMACY", "TACTICAL", "EXPANSION", "SHIPYARD"];
    public IReadOnlyList<int> Groups { get; }

    public void SelectGroup(int group)
    {
        if (!GroupEnabled(group)) return;
        SelectedGroup = group;
        Cursor = 0;
    }

    public void CycleGroup(int delta)
    {
        var enabled = Groups.Where(GroupEnabled).ToList();
        if (enabled.Count == 0) return;
        int index = enabled.IndexOf(SelectedGroup ?? enabled[0]);
        if (index < 0) index = delta < 0 ? 0 : -1;
        SelectGroup(enabled[(index + delta % enabled.Count + enabled.Count) % enabled.Count]);
    }

    // Native BuyableTechExistsInGroup ignores Hide Completed when enabling a tab
    // (WinTrek/loadout.cpp:1042-1083), but retains predecessor/mission-path gates.
    public bool GroupEnabled(int group) => _hasSide && Groups.Contains(group)
        && _defs.Any(b => b.GroupId == group && (CanBuy(b) || ShowAll) && PredecessorSatisfied(b));

    private bool PredecessorSatisfied(InvestmentBucket b) => ShowAll
        || !_predecessors.TryGetValue(b.Id, out var predecessor)
        || _defs.FirstOrDefault(d => d.Id == predecessor) is not { } prior || Completed(prior);

    private void ClampCursor() => Cursor = Math.Clamp(Cursor, 0, Math.Max(0, Items.Count - 1));

    public int? MyMoney => _myMoney;
    public int MaxDronesPerTeam => _maxDronesPerTeam;
    public bool MissionInProgress => _missionInProgress;
    public bool HasSide => _hasSide;

    public InvestmentPaneModel(
        IReadOnlyList<InvestmentBucket> defs,
        IReadOnlyDictionary<short, ClientWorld.BucketFunding>? funding,
        bool missionInProgress,
        bool hasSide,
        int? myMoney,
        IReadOnlyDictionary<short, int>? droneCountByHullId = null,
        int maxDronesPerTeam = 4,
        Func<short, bool>? stationCanBuild = null,
        bool showAll = false,
        bool hideCompleted = false,
        int cursor = 0,
        Func<InvestmentBucket, bool>? canBuy = null,
        IReadOnlyDictionary<short, short>? predecessors = null,
        IReadOnlyDictionary<short, int>? droneCountByTypeId = null,
        int? selectedGroup = null,
        IReadOnlyList<int>? groups = null)
    {
        _defs = defs ?? Array.Empty<InvestmentBucket>();
        _missionInProgress = missionInProgress;
        _hasSide = hasSide;
        _myMoney = myMoney;
        _droneCountByHullId = droneCountByHullId ?? EmptyDroneCounts;
        _maxDronesPerTeam = maxDronesPerTeam;
        _stationCanBuild = stationCanBuild ?? (_ => true);
        _canBuy = canBuy ?? (_ => true);
        _predecessors = predecessors ?? new Dictionary<short, short>();
        _droneCountByTypeId = droneCountByTypeId;
        Groups = groups ?? _defs.Select(b => b.GroupId).Where(g => g is >= 0 and <= 5).Distinct().Order().ToArray();
        SelectedGroup = selectedGroup;
        ShowAll = showAll;
        HideCompleted = hideCompleted;
        if (funding != null)
        {
            foreach (var (id, f) in funding)
            {
                _moneyById[id] = f.InvestedMoney;
                _timeMsById[id] = f.InvestedTimeMs;
            }
        }
        Cursor = Math.Clamp(cursor, 0, Math.Max(0, Items.Count - 1));
    }

    // ── per-bucket funding ────────────────────────────────────────────────

    public long MoneyOf(InvestmentBucket b) => _moneyById.TryGetValue(b.Id, out var m) ? m : 0;
    public long TimeMsOf(InvestmentBucket b) => _timeMsById.TryGetValue(b.Id, out var t) ? t : 0;
    public long TimeToBuildMs(InvestmentBucket b) => (long)b.TimeToBuildSec * 1000;

    /// <summary>Native completion gate <c>money ≥ price ∧ timeMs ≥ ttb·1000</c>
    /// (bucketigc.h:51).</summary>
    public bool Completed(InvestmentBucket b) => MoneyOf(b) >= b.Price && TimeMsOf(b) >= TimeToBuildMs(b);

    /// <summary>Native <c>100 * invested / price</c> (bucketigc.h:164-168).</summary>
    public int PercentBought(InvestmentBucket b) => b.Price > 0 ? (int)((100 * MoneyOf(b)) / b.Price) : 0;

    /// <summary>Money still owed (native <c>GetPrice() - GetMoney()</c>).</summary>
    public long RemainingMoney(InvestmentBucket b) => Math.Max(0, (long)b.Price - MoneyOf(b));

    /// <summary>Build time still owed, in milliseconds.</summary>
    public long RemainingTimeMs(InvestmentBucket b) => Math.Max(0, TimeToBuildMs(b) - TimeMsOf(b));

    /// <summary>Native side CanBuy, supplied from a live tech/station snapshot,
    /// independent of completion (Igc/sideigc.h:224-281).</summary>
    public bool CanBuy(InvestmentBucket b) => _canBuy(b);

    // ── display list ───────────────────────────────────────────────────────

    /// <summary>
    /// P487 — the visible item list, mirroring native <c>UpdateTechList</c>'s
    /// filter (loadout.cpp:1004-1006): a bucket shows iff
    /// <c>(CanBuy || ShowAll) &amp;&amp; !(HideCompleted &amp;&amp; Completed)</c>.
    /// Order is the synthesis order (<see cref="BucketCatalog.Describe"/>): ship
    /// type, developments, drone types, station types — so group headers emerge
    /// naturally.
    /// </summary>
    public IReadOnlyList<InvestmentBucket> Items
    {
        get
        {
            var result = new List<InvestmentBucket>(_defs.Count);
            foreach (var b in _defs)
            {
                if (SelectedGroup is { } group && b.GroupId != group) continue;
                bool displayed = (CanBuy(b) || ShowAll) && !(HideCompleted && Completed(b));
                if (!PredecessorSatisfied(b)) displayed = false;
                if (displayed) result.Add(b);
            }
            return result;
        }
    }

    /// <summary>The item under the cursor (clamped), or null when the list is empty.</summary>
    public InvestmentBucket? Current =>
        Items.Count > 0 ? Items[Math.Clamp(Cursor, 0, Items.Count - 1)] : null;

    public void MoveCursor(int delta)
    {
        int n = Items.Count;
        if (n == 0) { Cursor = 0; return; }
        Cursor = Math.Clamp(Cursor + delta, 0, n - 1);
    }

    public void ToggleShowAll() { ShowAll = !ShowAll; ClampCursor(); }
    public void ToggleHideCompleted() { HideCompleted = !HideCompleted; ClampCursor(); }

    // ── CanInvest / invest amounts ─────────────────────────────────────────

    /// <summary>
    /// P487 — the client's port of native <c>PurchasesPaneImpl::CanInvest</c>
    /// (loadout.cpp:1095-1142). Returns false when: the mission isn't in progress,
    /// the player has no side, the side cannot buy the bucket, the drone-type cap is reached, no station build-spot
    /// is available (host-fed), the player has no money, or the bucket is 100%
    /// funded.
    /// </summary>
    public bool CanInvest(InvestmentBucket b)
    {
        if (!_missionInProgress) return false;                 // MyMissionInProgress()
        if (!_hasSide) return false;                           // GetSide()
        if (!CanBuy(b)) return false;                          // pSide->CanBuy(pBucket)
        if (b.ItemType == BucketItemType.BitDroneType)
        {
            // CountDronesOfType >= nMaxDronesPerTeam -> refuse (loadout.cpp:1111-1120)
            int count = _droneCountByTypeId is { } counts ? counts.GetValueOrDefault(b.DroneTypeId)
                : _droneCountByHullId.TryGetValue(b.HullIdDrone, out var c) ? c : 0;
            if (count >= _maxDronesPerTeam) return false;
        }
        if (b.ItemType == BucketItemType.BitStationType)
        {
            // !BuildSpotFoundForStationType || FindBuilderForStationType != NULL -> refuse
            // (loadout.cpp:1124-1131); the host supplies the build-spot/builder verdict.
            if (!_stationCanBuild(b.StationTypeId)) return false;
        }
        if (!(_myMoney > 0)) return false;                     // GetMoney() <= 0
        if (PercentBought(b) >= 100) return false;             // already funded
        return true;
    }

    /// <summary>The full-invest amount native <c>OnInvest</c> puts in:
    /// <c>min(price - money, wallet)</c> (loadout.cpp:818-821).</summary>
    public int InvestAmount(InvestmentBucket b) =>
        (int)Math.Min(RemainingMoney(b), (long)(_myMoney ?? 0));

    /// <summary>The partial-invest amount native <c>OnPartial</c> computes
    /// (loadout.cpp:894-908): 10% of the TOTAL price, capped at the wallet, but
    /// never enough to complete the bucket (keeps at least 1cr outstanding; 0
    /// when the remaining cost is already 1).</summary>
    public int PartialInvestAmount(InvestmentBucket b)
    {
        long remaining = RemainingMoney(b);
        if (remaining == 1) return 0;                          // don't finish via right-click
        long tenPercent = (long)(b.Price * 0.1);
        long amount = Math.Min(tenPercent, (long)(_myMoney ?? 0));
        long money = MoneyOf(b);
        while (money + amount >= b.Price) amount -= 1;
        return (int)Math.Max(0, amount);
    }

    /// <summary>Full Invest on the cursor item (native OnInvest). Returns true +
    /// the bucket id / amount to donate, false when nothing investable is selected.</summary>
    public bool TryInvest(out short bucketId, out int amount)
    {
        bucketId = -1;
        amount = 0;
        if (Current is not { } b || !CanInvest(b)) return false;
        amount = InvestAmount(b);
        if (amount <= 0) return false;
        bucketId = b.Id;
        return true;
    }

    /// <summary>Partial Invest on the cursor item (native OnPartial, right-click).
    /// Returns false when nothing investable is selected or the computed partial is 0.</summary>
    public bool TryPartialInvest(out short bucketId, out int amount)
    {
        bucketId = -1;
        amount = 0;
        if (Current is not { } b || !CanInvest(b)) return false;
        amount = PartialInvestAmount(b);
        if (amount <= 0) return false;
        bucketId = b.Id;
        return true;
    }

    /// <summary>
    /// P759 — the pure half of the <c>TK_ConModeInvest</c> edge trigger
    /// (<c>WinTrek.cpp:10025-10033</c>): an edge press toggles the investment
    /// overlay only while not composing and while the mission allows developments
    /// (<c>AllowDevelopments()</c>). Kept static so the gate is headless-testable;
    /// <see cref="Game1.HandleInvestmentScreen"/> is the production caller.
    /// </summary>
    public static bool TogglePermitted(bool developments, bool composing, bool keyDown, bool wasDown)
        => developments && !composing && keyDown && !wasDown;
}
