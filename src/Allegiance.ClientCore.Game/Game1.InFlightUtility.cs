using System.Linq;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P626 — the in-flight utility pack slice of <see cref="Game1"/>: vote keys,
/// in-flight cargo/inventory selection (NextCargo / NextPart / PrevPart),
/// drop-cargo (TK_DropCargo) and reload (TK_Reload). Partial class — shares the
/// <c>Game1</c> type, so private field/method access across the split files is
/// unaffected. Hosts only the thin Game1 glue; the pure, testable arithmetic
/// lives in <c>Rendering.ChatScroll</c> / <c>Rendering.CargoSelector</c> / the
/// wire codecs.
/// </summary>
public sealed partial class Game1
{
    /// <summary>P626 — cast a vote on the active in-flight ballot (P187). Reads
    /// <see cref="ClientWorld.ActiveBallot"/> and sends a real FM_C_VOTE(19)
    /// naming that ballot's id; with no ballot in flight it posts a hint instead
    /// of silently doing nothing.</summary>
    private void CastVote(bool agree)
    {
        ClientWorld.PendingBallotView? b;
        lock (_session.Gate) b = _session.World.ActiveBallot;
        if (b is null)
        {
            lock (_session.Gate) _session.World.PostSystemLine("No active vote to cast.");
            Console.WriteLine("[monogame] vote: no active ballot");
            return;
        }
        _session.SendVote(b.BallotId, agree);
        Console.WriteLine($"[monogame] vote {(agree ? "YES" : "NO")} on ballot #{b.BallotId}");
        lock (_session.Gate)
            _session.World.PostSystemLine($"[VOTE] You voted {(agree ? "YES" : "NO")}.");
    }

    // ── P626 — in-flight cargo (NextCargo/NextPart/PrevPart/DropCargo/Reload) ──

    /// <summary>P626 — the local player's cargo hold entries (negative mounts),
    /// read under the world gate. Empty when not assigned a ship / no cargo.</summary>
    private List<ExpandedPartData> OwnCargo()
    {
        var world = _session.World;
        lock (_session.Gate)
        {
            if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var e) && e.Cargo is { } c)
                return c.ToList();
        }
        return new List<ExpandedPartData>();
    }

    /// <summary>P626 — advance (or retreat) the selected cargo part through the
    /// occupied cargo mounts, native's NextCargoPart direction (trekigc.cpp:
    /// 2620-2642) / PrevPart family. Updates <see cref="_selectedCargoMount"/>.</summary>
    private void CycleSelectedCargo(bool next)
    {
        var cargo = OwnCargo();
        var mounts = CargoSelector.OccupiedMounts(cargo);
        int sel = next
            ? CargoSelector.Next(_selectedCargoMount, mounts)
            : CargoSelector.Prev(_selectedCargoMount, mounts);
        _selectedCargoMount = sel;
        if (sel < 0)
        {
            lock (_session.Gate) _session.World.PostSystemLine("Cargo hold is empty.");
            Console.WriteLine("[monogame] cargo: empty hold, nothing selected");
            return;
        }
        var (pid, amt) = CargoAt(cargo, sel);
        Console.WriteLine($"[monogame] cargo: selected mount {sel} (Part #{pid} x{amt})");
    }

    /// <summary>P626 — drop the selected cargo part into space (native
    /// TK_DropCargo, WinTrek.cpp:9698-9730): sends FM_CS_DROP_PART with the
    /// selected negative mount; the server drops it and creates a treasure. With
    /// nothing selected it first tries to select the first occupied slot.</summary>
    private void TryDropCargo()
    {
        if (_selectedCargoMount < 0)
        {
            CycleSelectedCargo(next: true);
            if (_selectedCargoMount < 0) return;   // empty hold (hint already posted)
        }
        _session.SendDropCargo((sbyte)_selectedCargoMount);
        Console.WriteLine($"[monogame] cargo: DROP_CARGO mount {_selectedCargoMount} -> DROP_PART(33)");
    }

    /// <summary>P626 — reload from cargo (native TK_Reload, WinTrek.cpp:9674-9695
    /// → BaseClient::Reload clintlib.cpp:3425+): sends FM_CS_RELOAD with one
    /// NA-amount entry per occupied cargo slot; the server tops up pools /
    /// launchers and skips non-reloadable slots (P626 skip-and-continue).</summary>
    private void TryReload()
    {
        var cargo = OwnCargo();
        var entries = new List<ReloadData>();
        foreach (var c in cargo)
            if (CargoSelector.IsCargoMount(c.MountId))
                entries.Add(new ReloadData(-1, CargoSelector.SignedMount(c.MountId)));   // NA = consume whole item
        if (entries.Count == 0)
        {
            lock (_session.Gate) _session.World.PostSystemLine("Nothing to reload (cargo hold is empty).");
            Console.WriteLine("[monogame] reload: empty cargo hold");
            return;
        }
        _session.SendReload(entries);
        Console.WriteLine($"[monogame] reload: {entries.Count} cargo entry/entries -> RELOAD(129)");
    }

    private static (short PartId, short Amount) CargoAt(IReadOnlyList<ExpandedPartData> cargo, int mount)
    {
        foreach (var c in cargo)
            if (CargoSelector.SignedMount(c.MountId) == mount)
                return (c.PartId, c.Amount);
        return (0, 0);
    }

    /// <summary>P626 — draw the in-flight cargo/inventory readout: the selected
    /// part (the one NextCargo/NextPart/PrevPart highlight and DropCargo drops)
    /// plus the hold occupancy, as a small ASCII line under the chat log. Best-
    /// effort — no part-type metadata is available in flight, so entries render
    /// as "Part #id xN" (honest, never fabricated). Skipped when the hold is
    /// empty and nothing is selected.</summary>
    private void DrawCargoHud()
    {
        if (_directorActive || _state != ClientState.Flying) return;
        var cargo = OwnCargo();
        var mounts = CargoSelector.OccupiedMounts(cargo);
        if (cargo.Count == 0 && _selectedCargoMount < 0) return;

        // Compact hold list: "@<mount>#<id>x<N>" per occupied slot.
        var sb = new System.Text.StringBuilder("CARGO:");
        for (int i = 0; i < cargo.Count; i++)
        {
            var c = cargo[i];
            if (!CargoSelector.IsCargoMount(c.MountId)) continue;
            int m = CargoSelector.SignedMount(c.MountId);
            sb.Append($" @{m}#{c.PartId}x{c.Amount}");
        }
        if (_selectedCargoMount < 0)
        {
            sb.Append("  (none selected - , to select)");
        }
        else
        {
            var (pid, amt) = CargoAt(cargo, _selectedCargoMount);
            sb.Append($"  >> SELECTED mount {_selectedCargoMount} Part #{pid} x{amt} (Ctrl+, drop)");
        }

        string line = sb.ToString();
        if (line.Any(ch => ch < 32 || ch > 126))
            line = new string(line.Select(ch => ch < 32 || ch > 126 ? '?' : ch).ToArray());

        float w = GraphicsDevice.Viewport.Width;
        float h = GraphicsDevice.Viewport.Height;
        var sz = _hudSmall.MeasureString(line);
        float x = 20f, y = h - 132f;
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            _spriteBatch.Draw(_whitePixel, new Microsoft.Xna.Framework.Rectangle((int)x - 6, (int)y - 4, (int)(sz.X + 12), (int)(sz.Y + 8)),
                new Microsoft.Xna.Framework.Color(0.008f, 0.025f, 0.028f, 0.6f));
            _spriteBatch.DrawString(_hudSmall, line, new Microsoft.Xna.Framework.Vector2(x, y),
                new Microsoft.Xna.Framework.Color(0.78f, 0.88f, 0.85f));
        }
        finally
        {
            _spriteBatch.End();
        }
    }
}
