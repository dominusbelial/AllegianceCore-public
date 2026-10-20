using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// P752 — the TEAM PANE's selected-player controls, the port of native
/// <c>TeamPane</c>'s button row (<c>WinTrek/teampane.cpp:1104-1367</c>):
/// <list type="bullet">
/// <item><b>Donate and stop buttons</b> (teampane.cpp:1141-1245) — give the
/// selected player $100 (or $500, native's right-click), donate the whole
/// wallet as a standing auto-donate, and the leader-only stop-donating toggle
/// (<c>AUTODONATE</c> amount −1).</item>
/// <item><b>Take-me-to</b> (teampane.cpp:1248-1336) — always close the team
/// overlay and pick the selected player (<c>PickShip</c>, teampane.cpp:1330),
/// even when they are in another sector and the viewer isn't docked; the
/// camera jump to their sector is the only part gated on same-sector/docked.</item>
/// <item><b>Accept</b> (teampane.cpp:1337-1354) — the leader accepts a pending
/// join request (<c>POSITIONACK</c> accepted).</item>
/// </list>
/// The roster is the pane's own whole-mission scope (every side's players,
/// native's per-side list plus the port's cross-side roster), with the own
/// side's pending join requests appended for the leader exactly like native's
/// <c>ConcatinatedList(members, requests)</c> (teampane.cpp:987-995).
///
/// Pure decision logic: <see cref="Step"/> consumes keyboard edges and world
/// state and returns the intents; <c>Game1</c> performs the wire sends, so the
/// native rules are unit-testable without a graphics device (the same
/// discipline as <see cref="InvestmentFundingInput"/>).
/// </summary>
internal sealed class TeamPaneControls
{
    private readonly ActionPressEdges _edges = new();
    private int _selection = -1;   // index into the roster; -1 = nothing selected

    /// <summary>One selectable roster row: a seated player, or (for the leader)
    /// a pending join request on the own side.</summary>
    internal readonly record struct RosterRow(
        string Name, short SideId, short ShipId, short? SectorId, bool IsJoinRequest);

    /// <summary>The intents one step produced. At most one wire effect fires
    /// per edge (native's buttons are one action each); selection movement is
    /// reported separately so the pane can re-render.</summary>
    internal readonly record struct Result(
        PlayerDonate? Gift = null,
        AutoDonate? Auto = null,
        short? TakeMeToShipId = null,
        short? TakeMeToSectorId = null,
        short? AcceptShipId = null,
        bool SelectionChanged = false);

    internal int Selection => _selection;

    /// <summary>The selectable roster, in the pane's display order: every
    /// side's players grouped by side id then name (the order
    /// <c>TeamPaneLayout.Build</c> draws), then — for the leader — the own
    /// side's pending join requests (native teampane.cpp:987-995).</summary>
    internal static IReadOnlyList<RosterRow> BuildRoster(ClientWorld world)
    {
        var rows = new List<RosterRow>();
        foreach (var g in world.ScoreRoster.GroupBy(e => e.SideId).OrderBy(g => g.Key))
        {
            foreach (var e in g.OrderBy(e => e.CharacterName, StringComparer.Ordinal))
            {
                // Resolve the live ship entity for the id/sector (a roster name
                // without a live ship keeps a -1 id and an unknown sector).
                short shipId = -1;
                short? sector = null;
                foreach (var s in world.Ships.Values)
                {
                    if (s.SideId == g.Key && s.CharacterName == e.CharacterName)
                    {
                        shipId = s.ShipId;
                        sector = s.SectorId;
                        break;
                    }
                }
                rows.Add(new RosterRow(e.CharacterName, g.Key, shipId, sector, IsJoinRequest: false));
            }
        }
        if (world.IsTeamLeader && world.MySideId is { } mySide)
        {
            foreach (var req in world.PendingPositionRequests)
            {
                if (req.SideId != mySide) continue;
                string name = world.Ships.TryGetValue(req.ShipId, out var ent) && !string.IsNullOrEmpty(ent.CharacterName)
                    ? ent.CharacterName! : $"Pilot {req.ShipId}";
                rows.Add(new RosterRow(name, mySide, req.ShipId, null, IsJoinRequest: true));
            }
        }
        return rows;
    }

    /// <summary>Clamp a selection index into the roster (native's list keeps
    /// its selection when rows leave; a vanished selection clears).</summary>
    internal static int ClampSelection(int current, int count)
        => count == 0 ? -1 : current >= 0 && current < count ? current : -1;

    /// <summary>Advance the selection (PageDown): none → first, then forward with
    /// wrap-around (native's list navigation).</summary>
    internal static int NextSelection(int current, int count)
        => count == 0 ? -1 : (current + 1) % count;

    /// <summary>P752c — native's cross-side gift feedback line
    /// (teampane.cpp:1152-1158, 1181-1188): "You gave {name} ${given}. You now
    /// have ${left}." where <c>left = money − given</c> (never negative), NOT
    /// the pre-gift balance.</summary>
    internal static string CrossSideGiftLine(string name, int money, int given)
        => $"You gave {name} ${given}. You now have ${Math.Max(0, money - given)}.";

    /// <summary>
    /// One input step. <paramref name="active"/> is false while composing chat
    /// or when the pane is hidden — edges are still observed (so a held key
    /// cannot queue an action) but nothing fires. The funding edges reuse the
    /// investment pane's rebindable actions (<c>InvestDonate</c> $100,
    /// <c>InvestDonateLarge</c> $500, <c>InvestAutoDonate</c> wallet,
    /// <c>InvestToggleDonate</c> leader stop-toggle) exactly like native's one
    /// verb set acting on the pane's selected player; <c>TeamPaneNextPilot</c>
    /// (PageDown) cycles the selection and <c>TeamPaneAct</c> (Y) is take-me-to on
    /// a player row / accept on a join-request row (a pending ballot keeps Y
    /// for the vote, mirroring the ready-check's shared-key convention).
    /// </summary>
    internal Result Step(KeyboardState keys, ActionMap map, ClientWorld world, bool active, bool ballotActive)
    {
        bool Edge(string action) => _edges.Observe(action, map.IsDownMostSpecific(keys, action), !active);
        bool next = Edge("TeamPaneNextPilot");
        bool act = Edge("TeamPaneAct");
        bool small = Edge("InvestDonate");
        bool large = Edge("InvestDonateLarge");
        bool auto = Edge("InvestAutoDonate");
        bool toggle = Edge("InvestToggleDonate");

        var rows = BuildRoster(world);
        _selection = ClampSelection(_selection, rows.Count);
        if (next)
        {
            _selection = NextSelection(_selection, rows.Count);
            return new Result(SelectionChanged: true);
        }
        if (!active || _selection < 0 || _selection >= rows.Count)
            return default;
        var row = rows[_selection];

        // Funding verbs (native OnButtonDonate/OnRightButtonDonate/
        // OnButtonAutoDonate/OnButtonStopDonate, teampane.cpp:1141-1245):
        // no self-gifts, no gifts without money.
        int money = Math.Max(0, world.MyMoney ?? 0);
        if ((small || large) && money > 0 && row.ShipId != world.MyShipId && !row.IsJoinRequest)
            return new Result(Gift: new PlayerDonate
            {
                ShipID = row.ShipId,
                MoneyGiven = Math.Min(money, large ? 500 : 100),
            });
        if (toggle && world.IsTeamLeader && row.ShipId != world.MyShipId && !row.IsJoinRequest)
            return new Result(Auto: new AutoDonate { SidDonateTo = row.ShipId, Amount = -1 });
        if (auto && row.ShipId != world.MyShipId && !row.IsJoinRequest)
            return new Result(Auto: new AutoDonate { SidDonateTo = row.ShipId, Amount = money });

        // The single action key: accept a join request, otherwise take me to
        // the selected player (a pending ballot keeps Y for the vote).
        // Native OnButtonTakeMeTo (teampane.cpp:1288-1330) ALWAYS closes the
        // team overlay and picks the selected ship, even when the player is in
        // another sector and the viewer isn't docked — the camera jump is the
        // only part gated on sector/docked (that gate lives in
        // TakeMeToSelected). The sector carried is the ship-STATUS sector
        // (GetShipStatus().GetSectorID(), teampane.cpp:1305), NOT the live
        // entity's cluster; null means "unknown" and still picks the ship.
        if (act && !ballotActive)
        {
            if (row.IsJoinRequest)
                return new Result(AcceptShipId: row.ShipId);
            if (row.ShipId >= 0)
                return new Result(TakeMeToShipId: row.ShipId, TakeMeToSectorId: row.SectorId);
        }
        return default;
    }
}
