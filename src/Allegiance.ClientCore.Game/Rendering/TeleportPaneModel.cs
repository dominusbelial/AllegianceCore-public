using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P273 — the pure interaction model of the docked teleport pane (native's
/// <c>TeleportPane</c>, <c>WinTrek/teleportpane.cpp</c>). While the local ship
/// is docked this pane lists the destinations you can jump to:
/// <list type="bullet">
/// <item><b>Boardable same-side ships</b> — docked teammates' hulls that have
/// real turret hardpoints (<c>MaxFixedWeapons != MaxWeapons</c>, the native
/// <c>CanBoard</c> gate, <c>teleportpane.cpp:375-397</c>). Selecting one
/// boards it as a turret gunner via <c>BOARD_SHIP</c>.</item>
/// <item><b>Friendly restart-capable stations</b> — the side's stations that
/// have <c>c_sabmRestart</c> (<c>teleportpane.cpp:419-430</c>). Selecting one
/// teleports you there via <c>DOCKED(stationID)</c>.</item>
/// </list>
///
/// <para>Like <see cref="ShopModel"/>, this class is MonoGame-free and
/// headless-testable (the <see cref="ScoreboardLayout"/> discipline). It owns
/// the destination <em>filtering</em> (side / docked / turret-slots /
/// restart-capable) and the cursor, and its <see cref="Activate"/> returns a
/// <em>what-to-do</em> verdict the view layer turns into a real wire message
/// — the model itself never talks to the network.</para>
/// </summary>
public sealed class TeleportPaneModel
{
    /// <summary>The wire NA sentinel, matching <c>BoardShip.SidParent == -1</c>.</summary>
    public const short Na = -1;

    /// <summary>One boardable teammate ship shown in the pane.</summary>
    public readonly record struct ShipEntry(short Id, string Name, int TurretSlots);

    /// <summary>One friendly station shown in the pane.</summary>
    public readonly record struct StationEntry(short Id, string Name);

    /// <summary>The action the player chose when they pressed SELECT.</summary>
    public enum ActionKind
    {
        /// <summary>Nothing selected (empty list).</summary>
        None,
        /// <summary>Board <see cref="TeleportAction.TargetId"/> as a turret gunner.</summary>
        BoardShip,
        /// <summary>Teleport (relocate-dock) to station <see cref="TeleportAction.TargetId"/>.</summary>
        TeleportToStation,
    }

    public readonly record struct TeleportAction(ActionKind Kind, short TargetId)
    {
        public static readonly TeleportAction Nothing = new(ActionKind.None, Na);
    }

    private readonly List<ShipEntry> _ships = new();
    private readonly List<StationEntry> _stations = new();

    /// <summary>Index into the flattened ships-then-stations list; clamped on move.</summary>
    public int Cursor { get; private set; }

    /// <summary>Total row count (boardable ships + friendly stations).</summary>
    public int RowCount => _ships.Count + _stations.Count;

    /// <summary>True when there is no destination to choose.</summary>
    public bool IsEmpty => RowCount == 0;

    /// <summary>Boardable same-side ships (sorted by name for a stable list).</summary>
    public IReadOnlyList<ShipEntry> Ships => _ships;

    /// <summary>Friendly restart-capable stations (sorted by name).</summary>
    public IReadOnlyList<StationEntry> Stations => _stations;

    private TeleportPaneModel() { }

    /// <summary>
    /// Build the destination list from the live world snapshot. Pure filtering —
    /// the native <c>RebuildList</c> (<c>teleportpane.cpp:399-435</c>):
    /// <list type="bullet">
    /// <item>ships: same side, not self, docked (<c>StationId != NA</c>), and the
    /// hull actually has turret hardpoints (<c>CanBoard</c>,
    /// <c>teleportpane.cpp:375-397</c> — the port omits native's
    /// <c>PilotType &gt;= c_ptPlayer</c> pilot-or-cheater clause, which this port
    /// has no concept of);</item>
    /// <item>stations: same side, not the one we're currently at, and
    /// <c>c_sabmRestart</c>-capable when a station-type lookup is available (an
    /// absent catalog degrades to include the station — the server re-enforces
    /// restart capability on the <c>DOCKED</c> teleport).</item>
    /// </list>
    /// </summary>
    public static TeleportPaneModel Build(
        short? ownShipId,
        short? ownSideId,
        short ownStationId,
        IReadOnlyCollection<ShipView> ships,
        IReadOnlyCollection<StationView> stations,
        Func<short, DataHullTypeIGC?> hullLookup,
        Func<short, DataStationTypeIGC?> stationTypeLookup)
    {
        var m = new TeleportPaneModel();
        if (ownSideId is not { } side)
            return m;                                   // no side → nothing meaningful to list

        foreach (var s in ships)
        {
            if (s.SideId != side) continue;             // GetSide() == trekClient.GetSide(), teleportpane.cpp:380
            if (s.Id == ownShipId) continue;            // pship != trekClient.GetShip(), teleportpane.cpp:379
            if (s.StationId == Na) continue;            // LastSeenState() == c_ssDocked, teleportpane.cpp:381
            var hull = hullLookup(s.HullId);
            if (hull is not { } h) continue;
            // CanBoard turret-slot gate: the hull must actually HAVE turret
            // hardpoints (teleportpane.cpp:390). TurretSlots = MaxWeapons - MaxFixedWeapons.
            int slots = h.MaxWeapons - h.MaxFixedWeapons;
            if (slots <= 0) continue;
            m._ships.Add(new ShipEntry(s.Id, h.Name, slots));
        }

        foreach (var st in stations)
        {
            if (st.SideId != side) continue;            // side's stations, teleportpane.cpp:419-423
            if (st.Id == ownStationId) continue;        // the one we're already at — no-op to pick it
            var stype = stationTypeLookup(st.StationTypeId);
            if (stype is { } stp && !stp.HasCapability(DataStationTypeIGC.SabmRestart))
                continue;                               // c_sabmRestart, teleportpane.cpp:426
            m._stations.Add(new StationEntry(st.Id, st.Name));
        }

        m._ships.Sort((a, b) =>
        {
            int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });
        m._stations.Sort((a, b) =>
        {
            int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });
        return m;
    }

    /// <summary>Move the cursor by <paramref name="delta"/> rows, clamped to bounds.</summary>
    public void MoveCursor(int delta)
    {
        Cursor = Math.Clamp(Cursor + delta, 0, Math.Max(0, RowCount - 1));
    }

    /// <summary>Whether the cursor currently rests on a boardable ship row.</summary>
    public bool CursorOnShip => _ships.Count > 0 && Cursor < _ships.Count;

    /// <summary>
    /// The verdict for pressing SELECT on the current row — a pure
    /// classification the caller turns into a real <c>BOARD_SHIP</c> or
    /// <c>DOCKED(stationID)</c> message (mirroring native
    /// <c>OnButtonTeleport</c>, <c>teleportpane.cpp:296-361</c>).
    /// </summary>
    public TeleportAction Activate()
    {
        if (RowCount == 0) return TeleportAction.Nothing;
        if (CursorOnShip) return new TeleportAction(ActionKind.BoardShip, _ships[Cursor].Id);
        return new TeleportAction(ActionKind.TeleportToStation, _stations[Cursor - _ships.Count].Id);
    }
}

/// <summary>A wire-friendly projection of a teammate ship for the pane's
/// filtering (the minimal fields <c>CanBoard</c> reads). Translated from
/// <c>ShipEntity</c> by the view layer; kept primitive so the model is
/// headless-testable without a graphics/client-core dependency.</summary>
public readonly record struct ShipView(short Id, short SideId, short StationId, short HullId);

/// <summary>A wire-friendly projection of a station for the pane's filtering.
/// Translated from <c>DataStationIGC</c> by the view layer.</summary>
public readonly record struct StationView(short Id, short SideId, short StationTypeId, string Name);
