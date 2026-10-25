using Allegiance.ClientCore.MonoGame.Career;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P808c — draws the career screens (character info + leaderboard) in the
/// MonoGame client from the shared P808b screen models, in BOTH HUD styles.
/// The behaviour lives entirely in the shared models
/// (<see cref="CharacterInfoModel"/> / <see cref="LeaderboardModel"/>); this
/// renderer only presents them, choosing colours/layout by
/// <see cref="HudSettings.Style"/> (Classic flat cyan-on-dark + thin rules;
/// Modern brighter filled panels + stronger selection) so the HUD-parity rule
/// is met: the same model renders either style.
/// </summary>
public static class CareerScreenRenderer
{
    private const float RowH = 20f;
    private const float HeaderY = 118f;

    /// <summary>Leaderboard column headings shown in the pane (native
    /// <c>leaderboardscreen.cpp:59-76</c>; a legible 10-column set — the model
    /// still sorts by all 14 native columns, these are the ones rendered).</summary>
    private static readonly string[] Columns =
    {
        "#", "Pilot", "Rank", "Rating", "Games", "Time", "Wins", "Kills", "Deaths", "Score",
    };
    private const float HeaderScale = 0.55f;
    private const float DataScale = 0.9f;

    public static void Draw(
        SpriteBatch sb, SpriteFont hudSmall, SpriteFont hudBig, Texture2D whitePixel,
        HudSettings hud, CharacterInfoModel? info, LeaderboardModel? board,
        bool boardMode, int cursor, bool editingDesc, string descBuffer, string status,
        string viewedAccountId, float w, float h)
    {
        bool modern = hud.Style == HudStyle.Modern;
        Color accent = new(0.25f, 0.75f, 0.72f);
        Color text = new(0.88f, 0.96f, 0.95f);
        Color dim = new(0.55f, 0.70f, 0.68f);
        Color panel = modern ? new Color(0.055f, 0.085f, 0.095f) : new Color(0.045f, 0.065f, 0.07f);
        Color selFill = modern ? new Color(0.12f, 0.42f, 0.40f, 0.75f) : new Color(0.075f, 0.30f, 0.28f, 0.6f);

        // Title.
        string title = boardMode ? "[ LEADERBOARD ]" : "[ CHARACTER INFO ]";
        var tsz = hudBig.MeasureString(title);
        sb.DrawString(hudBig, title, new Vector2((w - tsz.X) / 2f, 24f), accent);

        // Mode tabs + Back.
        DrawTab(sb, hudSmall, whitePixel, "Career Info", 0, !boardMode, modern, accent, selFill, dim, w);
        DrawTab(sb, hudSmall, whitePixel, "Leaderboard", 1, boardMode, modern, accent, selFill, dim, w);
        DrawTab(sb, hudSmall, whitePixel, "Back", 2, false, modern, accent, selFill, dim, w);

        // Panel behind the table.
        int panelW = (int)Math.Min(w - 60f, 980f);
        sb.Draw(whitePixel, new Rectangle((int)((w - panelW) / 2f), (int)HeaderY - 26, panelW, (int)(h - HeaderY - 76f)), panel);

        if (boardMode)
            DrawBoard(sb, hudSmall, whitePixel, board, cursor, modern, accent, text, dim, selFill, w, h);
        else
            DrawInfo(sb, hudSmall, whitePixel, info, editingDesc, descBuffer, modern, accent, text, dim, selFill, viewedAccountId, w, h);

        // Footer controls + status.
        string controls = boardMode
            ? "UP/DOWN move   ENTER profile   S search   F find-me   T top   PGUP/PGDN page   <-/-> sort   ESC back"
            : (editingDesc ? "TYPE description   ENTER apply   ESC cancel" : "LEFT/RIGHT civ   E edit description   TAB leaderboard   ESC back");
        var csz = hudSmall.MeasureString(controls);
        sb.DrawString(hudSmall, controls, new Vector2((w - csz.X) / 2f, h - 34f), dim);
        if (status.Length > 0)
        {
            var ss = hudSmall.MeasureString(status);
            sb.DrawString(hudSmall, status, new Vector2((w - ss.X) / 2f, h - 56f), new Color(1f, 0.62f, 0.45f));
        }
    }

    private static void DrawTab(SpriteBatch sb, SpriteFont f, Texture2D px, string label, int idx, bool active,
        bool modern, Color accent, Color selFill, Color dim, float w)
    {
        var sz = f.MeasureString(label);
        float x = w / 2f + (idx - 1) * 150f - sz.X / 2f;
        float y = 66f;
        if (active)
        {
            sb.Draw(px, new Rectangle((int)x - 6, (int)y - 3, (int)sz.X + 12, (int)sz.Y + 6), selFill);
            sb.Draw(px, new Rectangle((int)x - 6, (int)y + (int)sz.Y + 2, (int)sz.X + 12, 2), accent);
        }
        sb.DrawString(f, label, new Vector2(x, y), active ? accent : dim);
    }

    private static void DrawInfo(SpriteBatch sb, SpriteFont f, Texture2D px, CharacterInfoModel? info, bool editingDesc,
        string descBuffer, bool modern, Color accent, Color text, Color dim, Color selFill, string viewedAccountId,
        float w, float h)
    {
        float cx = w / 2f;
        if (info is null || info.Callsign.Length == 0)
        {
            var msg = viewedAccountId.Length == 0
                ? "No pilot selected. Sign in (F2) or open a profile from the leaderboard."
                : "Loading character info...";
            var sz = f.MeasureString(msg);
            sb.DrawString(f, msg, new Vector2((w - sz.X) / 2f, h * 0.45f), dim);
            return;
        }

        // Identity header.
        var nameSz = f.MeasureString(info.Callsign);
        sb.DrawString(f, info.Callsign, new Vector2(cx - nameSz.X / 2f, HeaderY), text);

        // Civ selector row.
        float cy = HeaderY + 34f;
        DrawCivRow(sb, f, info, accent, dim, cx, cy);

        // Description block.
        float dy = cy + 40f;
        sb.DrawString(f, "Profile", new Vector2(cx - 330f, dy), accent);
        string desc = editingDesc ? descBuffer : info.Description;
        string shown = desc.Length == 0 ? "(no description set - E to add one)" : desc;
        string wrapped = Wrap(f, shown, 350f);   // left column only: the stats grid starts at cx + 40
        float ty = dy + 26f;
        foreach (var line in wrapped.Split('\n'))
        {
            if (line.Length == 0) continue;
            sb.DrawString(f, line, new Vector2(cx - 330f, ty), editingDesc ? new Color(1f, 0.9f, 0.6f) : text);
            ty += RowH;
        }
        if (editingDesc)
            sb.Draw(px, new Rectangle((int)(cx - 330f), (int)ty, 350, 2), accent);

        // Stats grid.
        var stats = info.SelectedStats;
        float sx = cx + 40f;
        float sy = dy;
        sb.DrawString(f, "Career by civilization", new Vector2(sx, sy), accent);
        if (stats is { } s)
        {
            const float gap = 30f;
            sb.DrawString(f, "Rank:  " + info.RankName, new Vector2(sx, sy + gap), text);
            sb.DrawString(f, "Score: " + s.Score.ToString("0"), new Vector2(sx, sy + gap * 2), text);
            sb.DrawString(f, "Rating:" + s.Rating, new Vector2(sx, sy + gap * 3), text);
            sb.DrawString(f, "Time:  " + (s.MinutesPlayed / 60) + "h " + (s.MinutesPlayed % 60) + "m", new Vector2(sx, sy + gap * 4), text);
            sb.DrawString(f, "Kills: " + s.Kills + "   Deaths: " + s.Deaths, new Vector2(sx, sy + gap * 5), text);
            sb.DrawString(f, "Wins:  " + s.Wins + "   Losses: " + s.Losses, new Vector2(sx, sy + gap * 6), text);
            sb.DrawString(f, "BaseKills: " + s.BaseKills + "   BaseCaps: " + s.BaseCaptures, new Vector2(sx, sy + gap * 7), text);
        }
        else
        {
            sb.DrawString(f, "(no career data for this selection)", new Vector2(sx, sy + 26f), dim);
        }
    }

    private static void DrawCivRow(SpriteBatch sb, SpriteFont f, CharacterInfoModel info, Color accent, Color dim, float cx, float cy)
    {
        float x = cx - 170f;
        foreach (string name in info.CivNames)
        {
            var sz = f.MeasureString(name);
            bool sel = info.CivNames[info.SelectedCiv] == name;
            sb.DrawString(f, (sel ? "[ " : "  ") + name + (sel ? " ]" : " "), new Vector2(x, cy), sel ? accent : dim);
            x += f.MeasureString((sel ? "[ " : "  ") + name + (sel ? " ]" : " ")).X;
        }
    }

    private static void DrawBoard(SpriteBatch sb, SpriteFont f, Texture2D px, LeaderboardModel? board, int cursor,
        bool modern, Color accent, Color text, Color dim, Color selFill, float w, float h)
    {
        float left = (w - Math.Min(w - 60f, 960f)) / 2f + 10f;
        if (board is null || board.Fail)
        {
            string msg = board?.FailReason ?? "Leaderboard loading\u2026";
            var sz = f.MeasureString(msg);
            sb.DrawString(f, msg, new Vector2(left, HeaderY + 40f), new Color(1f, 0.62f, 0.45f));
            return;
        }
        var rows = board.VisibleRows();
        if (rows.Count == 0)
        {
            sb.DrawString(f, "No players yet on this leaderboard.", new Vector2(left, HeaderY + 40f), dim);
            return;
        }

        // Per-column widths (Pilot/Rank wide enough for callsigns + rank names).
        int[] widths = { 44, 150, 220, 74, 64, 62, 60, 66, 70, 82 };   // Rank fits "Lieutenant Commander"

        // Header row (scaled so the wide labels fit the narrow numeric columns).
        float[] xs = new float[Columns.Length];
        float x = left;
        for (int c = 0; c < Columns.Length; c++)
        {
            xs[c] = x;
            Color col = (c == ColumnIndex(board.SortColumn)) ? accent : dim;
            sb.DrawString(f, Columns[c], new Vector2(x, HeaderY), col, 0, Vector2.Zero, HeaderScale, SpriteEffects.None, 0);
            x += widths[c];
        }
        sb.Draw(px, new Rectangle((int)left, (int)(HeaderY + 20f), (int)(x - left), 1), dim);

        float y = HeaderY + 30f;
        for (int i = 0; i < rows.Count && y < h - 70f; i++)
        {
            var e = rows[i];
            if (i == cursor)
                sb.Draw(px, new Rectangle((int)left - 6, (int)y - 2, (int)(x - left + 12), (int)RowH), selFill);
            string name = ClipToWidth(f, e.Callsign + (e.AccountId == board.BasisAccountId ? " *" : ""), widths[1] - 4, DataScale);
            string[] vals =
            {
                e.Position.ToString(), name, ClipToWidth(f, RankName(e.Rank), widths[2] - 4, DataScale),
                e.Rating.ToString(), e.GamesPlayed.ToString(),
                (e.MinutesPlayed / 60) + "h", e.Wins.ToString(), e.Kills.ToString(), e.Deaths.ToString(),
                e.TotalScore.ToString("0"),
            };
            for (int c = 0; c < vals.Length; c++)
                sb.DrawString(f, vals[c], new Vector2(xs[c], y), text, 0, Vector2.Zero, DataScale, SpriteEffects.None, 0);
            y += RowH;
        }
    }

    /// <summary>Clip a cell's text to a pixel width so a long rank name or
    /// callsign never bleeds into the neighbouring column.</summary>
    private static string ClipToWidth(SpriteFont f, string text, float width, float scale)
    {
        if (f.MeasureString(text).X * scale <= width) return text;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (f.MeasureString(text[..mid]).X * scale <= width) lo = mid; else hi = mid - 1;
        }
        return lo >= 1 ? text[..lo] : "";
    }

    private static int ColumnIndex(LeaderboardModel.StatsColumn col) => col switch
    {
        LeaderboardModel.StatsColumn.PlayerName => 1,
        LeaderboardModel.StatsColumn.Rank => 2,
        LeaderboardModel.StatsColumn.OverallRating or LeaderboardModel.StatsColumn.CombatRating => 3,
        LeaderboardModel.StatsColumn.GamesPlayed => 4,
        LeaderboardModel.StatsColumn.TimePlayed => 5,
        LeaderboardModel.StatsColumn.Wins => 6,
        LeaderboardModel.StatsColumn.Kills => 7,
        LeaderboardModel.StatsColumn.Deaths => 8,
        _ => 9,   // Score (and Position / loss / base columns not rendered)
    };

    private static string RankName(short r) => CareerRanks.Name(r);

    private static string Wrap(SpriteFont f, string text, float width)
    {
        var lines = new List<string>();
        string cur = "";
        foreach (string word in text.Split(' '))
        {
            string trial = cur.Length == 0 ? word : cur + " " + word;
            if (f.MeasureString(trial).X > width && cur.Length > 0) { lines.Add(cur); cur = word; }
            else cur = trial;
        }
        if (cur.Length > 0) lines.Add(cur);
        return string.Join('\n', lines);
    }
}
