// P1055 — the CONTENT side of the screen audit: every text run and panel/box the shared
// content renderers paint, recorded at the geometry the renderer itself resolves, measured
// with the REAL HUD fonts. This is what makes two pieces of the game's own text comparable
// (the chrome-vs-content classes of P1042b/P1042c never could see them).
//
// Sources (one per family, the same objects the draw path uses):
//   intro      IntroMainFrame (IntroLayout.Compute result, the painter's anchors)
//   view       IntroViewFrame   (ScreenRow/ScreenTextRun at their resolved anchors)
//   career     CareerScreenRenderer's own title/tab/status/controls rectangles + PanelRect
//   panels     ScreenPanelFrame (every PanelTextRow at its resolved anchor + the panel box)
//   scoreboard ScoreboardLayout.Layout (the renderer's own column origins + cells)
//   starmap    the renderer's own title/alert/site-label anchors + SectorInfoPaneRenderer.Box
//              and SectorInfoPaneLayout.LineOrigins for the pane's lines and probe row
//   gameover   GameOverScreenRenderer's panel header arithmetic (title/winner/reason)
//   hangar     reported by HangarScreenRenderer itself (its IScreenRectAudit Content calls)
//
// Loadout V2 is NOT enumerated: its text placement lives inside LoadoutV2Renderer (the rows
// carry no rectangles), and the report states that as the audit's one coverage gap.
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Microsoft.Xna.Framework;
using P1042Proof;

sealed partial class ScreenProof
{
    private void CollectContent(ScreenRecorder rec, Scene scene, int w, int h)
    {
        switch (scene.Family)
        {
            case "intro": CollectIntro(rec, scene, w, h); break;
            case "view": CollectView(rec, scene, w, h); break;
            case "career": CollectCareer(rec, w, h); break;
            case "sideselect":
            case "readycheck":
            case "briefing":
            case "teampane":
            case "quickchat":
            case "keybind":
            case "connectionlost":
                CollectPanel(rec, scene, w, h);
                break;
            case "scoreboard": CollectScoreboard(rec, w, h); break;
            case "starmap": CollectStarmap(rec, w, h); break;
            case "gameover": CollectGameOver(rec, w, h); break;
        }
    }

    /// <summary>The intro main menu's runs: title, subtitle, the label rows, pilot field,
    /// identity line and the footer hint — exactly the anchors <c>IntroScreenPainter</c> draws.</summary>
    private void CollectIntro(ScreenRecorder rec, Scene scene, int w, int h)
    {
        var frame = IntroFrame(w, h, scene.State, pilotFocused: scene.State == 0);
        float cx = w / 2f;
        var tsz = rec.Measure(frame.Title, true, 1f);
        rec.ContentText("intro.title", "intro", frame.Title, new Vector2((w - tsz.X) / 2f, 28f), 1f, true);
        if (frame.Subtitle.Length > 0)
        {
            var ssz = rec.Measure(frame.Subtitle, false, 1f);
            rec.ContentText("intro.subtitle", "intro", frame.Subtitle, new Vector2((w - ssz.X) / 2f, 82f), 1f);
        }
        var lay = frame.Layout;
        float y = lay.Rows.Length > 0 ? lay.Rows[0].Y + 4f : h * 0.24f;
        for (int i = 0; i < frame.Labels.Count; i++)
        {
            string label = frame.Labels[i];
            var sz = rec.Measure(label, false, 1f);
            rec.ContentText($"intro.row{i}", "intro", label,
                new Vector2(cx - sz.X * lay.RowTextScale / 2f, y + 2f), lay.RowTextScale);
            y += lay.RowHeight;
        }
        var nsz = rec.Measure(frame.PilotLabel, false, 1f);
        rec.ContentText("intro.pilot", "intro", frame.PilotLabel, new Vector2(cx - nsz.X / 2f, lay.PilotY), 1f);
        var isz = rec.Measure(frame.IdentityLine, false, frame.IdentityScale);
        rec.ContentText("intro.identity", "intro", frame.IdentityLine,
            new Vector2((w - isz.X) / 2f, lay.IdentityY), frame.IdentityScale);
        var fsz = rec.Measure(frame.Hint, false, frame.HintScale);
        rec.ContentText("intro.hint", "intro", frame.Hint,
            new Vector2((w - fsz.X) / 2f, lay.FooterY), frame.HintScale);
    }

    /// <summary>A pre-game sub-view: the title/subtitle and every resolved row/line run.</summary>
    private void CollectView(ScreenRecorder rec, Scene scene, int w, int h)
    {
        var frame = ViewFrame(scene.State, w, h);
        if (frame.Title.Length > 0)
        {
            var tsz = rec.Measure(frame.Title, true, 1f);
            rec.ContentText("view.title", frame.View, frame.Title, new Vector2((w - tsz.X) / 2f, 28f), 1f, true);
        }
        if (frame.Subtitle.Length > 0)
        {
            var ssz = rec.Measure(frame.Subtitle, false, 1f);
            rec.ContentText("view.subtitle", frame.View, frame.Subtitle, new Vector2((w - ssz.X) / 2f, 82f), 1f);
        }
        for (int i = 0; i < frame.Rows.Count; i++)
        {
            var row = frame.Rows[i];
            rec.ContentText($"view.row{i}", frame.View, row.Text, row.Position, row.Scale);
        }
        for (int i = 0; i < frame.Lines.Count; i++)
        {
            var line = frame.Lines[i];
            rec.ContentText($"view.line{i}", frame.View, line.Text, line.Position, line.Scale);
        }
    }

    /// <summary>The career screen's own runs (title, tabs, footer controls, status) and its
    /// table panel box.</summary>
    private void CollectCareer(ScreenRecorder rec, int w, int h)
    {
        string title = CareerScreenRenderer.TitleFor(false, false, false);
        var tsz = rec.Measure(title, true, 1f);
        rec.ContentText("career.title", "career", title, new Vector2((w - tsz.X) / 2f, CareerScreenRenderer.TitleTop), 1f, true);
        for (int i = 0; i < CareerScreenRenderer.TabLabels.Length; i++)
        {
            string label = CareerScreenRenderer.TabLabels[i];
            var sz = rec.Measure(label, false, 1f);
            rec.ContentText($"career.tab{i}", "career", label,
                new Vector2(w / 2f + (i - 1) * 150f - sz.X / 2f, 66f), 1f);
        }
        const string controls = "LEFT/RIGHT civ   E edit description   TAB leaderboard   ESC back";
        var csz = rec.Measure(controls, false, 1f);
        rec.ContentText("career.controls", "career", controls, new Vector2((w - csz.X) / 2f, h - 34f), 1f);
        const string status = "Not signed in.";
        var ssz = rec.Measure(status, false, 1f);
        rec.ContentText("career.status", "career", status, new Vector2((w - ssz.X) / 2f, h - 56f), 1f);
        var panel = CareerScreenRenderer.PanelRect(w, h);
        rec.ContentPanel("career", new RectangleF(panel.X, panel.Y, panel.Width, panel.Height));
    }

    /// <summary>A panel family: every text run of the frame at its resolved anchor, and the
    /// panel's own box (runs inside it are its own, so they are not collisions).</summary>
    private void CollectPanel(ScreenRecorder rec, Scene scene, int w, int h)
    {
        var frame = PanelFrame(scene.Family, scene.State, w, h);
        for (int i = 0; i < frame.Rows.Count; i++)
        {
            var row = frame.Rows[i];
            rec.ContentText($"{scene.Family}.{row.Id}", scene.Family, row.Text, row.Position,
                row.Scale, row.Large);
        }
        rec.ContentPanel(scene.Family, frame.Panel);
    }

    /// <summary>The leaderboard: the renderer's own header/row cells at ScoreboardLayout's
    /// column origins, plus its panel box.</summary>
    private void CollectScoreboard(ScreenRecorder rec, int w, int h)
    {
        var rows = _scoreRows;
        var geo = ScoreboardLayout.Layout(rows, s => rec.Measure(s, false, 1f).X, w, h);
        float[] xs = geo.ColumnX;
        float panelY = geo.Panel.Y;
        rec.ContentText("scoreboard.heading", "scoreboard", "LEADERBOARD  (B TOGGLE)",
            new Vector2(xs[0], panelY + ScoreboardLayout.Padding - 14f), 1f);
        var headers = ScoreboardLayout.Row.HeaderCells;
        for (int c = 0; c < headers.Length; c++)
            rec.ContentText($"scoreboard.header{c}", "scoreboard", headers[c], new Vector2(xs[c], geo.HeaderY), 1f);
        float rowY = geo.HeaderY + geo.RowHeight;
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            string[] cells = [row.Position, row.Name, row.SideLabel, row.RankName, row.Kills, row.Deaths];
            for (int c = 0; c < cells.Length; c++)
                rec.ContentText($"scoreboard.row{r}.{c}", "scoreboard", cells[c], new Vector2(xs[c], rowY), 1f);
            rowY += geo.RowHeight;
        }
        rec.ContentPanel("scoreboard",
            new RectangleF(geo.Panel.X, geo.Panel.Y, geo.Panel.Width, geo.Panel.Height));
    }

    /// <summary>The starmap: the port header line, the selected sector's alert line (the
    /// "Spotted …" run the sector-info pane used to paint over), every sector label, and the
    /// info pane's own lines/probe row at <see cref="SectorInfoPaneLayout.LineOrigins"/> — plus
    /// the map panel box and the info pane box.</summary>
    private void CollectStarmap(ScreenRecorder rec, int w, int h)
    {
        var frame = StarmapFrame(w, h);
        float panelX = frame.Panel.X, panelY = frame.Panel.Y;
        const float border = 26f;
        var layout = StarmapLayout.Fit(frame.Sites, panelX, panelY, frame.PanelW, frame.PanelH, border);

        string currentName = frame.CurrentSector is { } cur && layout.TryGetName(cur, out var cn) && cn.Length > 0
            ? cn : "?";
        string title = $"STARMAP  |  YOU: {currentName}  |  TAB TOGGLE";
        rec.ContentText("starmap.title", "map", title, new Vector2(panelX + border, panelY + 10f), 1f);

        // P1055 — the selected sector's warning is no longer a run under the header: it is the
        // sector-info pane's own first line (native's Point(0,0), sectormap.cpp:517-534), and
        // the pane block below records it there. `--pre-starmap` rebuilds the PRE-FIX state
        // (the alert under the header, the pane with no warning line) so the audit can be shown
        // to flag the pane-over-alert class before the fix.
        if (_options.PreStarmap && frame.SelectedSector is { } sel && frame.Warnings is not null
            && layout.TryGetName(sel, out var selName) && selName.Length > 0)
        {
            string wtxt = StarmapThreatPresentation.WarningText(frame.Warnings(sel));
            if (wtxt.Length > 0)
            {
                var titleSize = rec.Measure(title, false, 1f);
                var alertPos = new Vector2(panelX + border, panelY + 10f + titleSize.Y + 3f);
                rec.ContentText("starmap.alert", "map", wtxt, alertPos, 1f);
                var wsz = rec.Measure(wtxt, false, 1f);
                rec.ContentText("starmap.alert.sector", "map", selName,
                    new Vector2(panelX + border + wsz.X + 10f, alertPos.Y), 1f);
            }
        }

        foreach (var site in frame.Sites)
        {
            if (!layout.TryGetPoint(site.Id, out var pt)) continue;
            var sz = rec.Measure(site.Name, false, 1f);
            rec.ContentText($"starmap.site{site.Id}", "map", site.Name,
                new Vector2(pt.X - sz.X / 2f, pt.Y + 9f), 1f);
            if (site.Id == frame.CurrentSector)
            {
                var ysz = rec.Measure("YOU", false, 1f);
                rec.ContentText("starmap.you", "map", "YOU",
                    new Vector2(pt.X - ysz.X / 2f, pt.Y - 8f - ysz.Y), 1f);
            }
        }
        rec.ContentPanel("map", frame.Panel);

        if (frame.InfoPane is { } pane && (pane.DisplayLines.Count > 0 || pane.ShowsOwnProbes))
        {
            var geo = SectorInfoPaneRenderer.Box(pane, panelX, panelY, frame.PanelW, frame.PanelH,
                _small.LineSpacing, s => _small.MeasureString(s).X);
            rec.ContentPanel("pane", geo.Box);
            var origins = SectorInfoPaneLayout.LineOrigins(
                new System.Numerics.Vector2(geo.Box.X + 12f, geo.Box.Y + 8f), _small.LineSpacing, geo.LineCount);
            int k = 0;
            if (geo.HasWarning)
            {
                rec.ContentText("pane.warning", "pane", pane.WarningText!,
                    new Vector2(origins[k].X, origins[k].Y), 1f);
                k++;
            }
            for (int i = 0; i < pane.DisplayLines.Count; i++, k++)
                rec.ContentText($"pane.line{i}", "pane", pane.DisplayLines[i],
                    new Vector2(origins[k].X, origins[k].Y), 1f);
            if (pane.ShowsOwnProbes && k < origins.Count)
            {
                // The probe row's text sits one glyph box + 6 px right of the row origin
                // (SectorInfoPaneRenderer.Draw); the diamond itself is a fill, not text.
                rec.ContentText("pane.probes", "pane", $"PROBES  {pane.OwnProbeCount}",
                    new Vector2(origins[k].X + 12f + 6f, origins[k].Y), 1f);
            }
        }
    }

    /// <summary>The game-over panel's header runs at the renderer's own arithmetic
    /// (GameOverScreenRenderer.Draw: title scale/centring, winner banner, reason lines).</summary>
    private void CollectGameOver(ScreenRecorder rec, int w, int h)
    {
        var frame = GameOverFrame(w, h);
        float panelW = frame.Panel.Width, panelY = frame.Panel.Y;
        float panelX = frame.Panel.X;
        const float pad = 30f;
        var tsz = rec.Measure(frame.Info.Title, true, 1f);
        float titleScale = MathF.Min(1f, (panelW - 2f * pad) / tsz.X);
        rec.ContentText("gameover.title", "gameover", frame.Info.Title,
            new Vector2(panelX + (panelW - tsz.X * titleScale) / 2f, panelY + 18f), titleScale, true);
        float cursorY = panelY + 18f + rec.Measure("Mg", true, titleScale).Y + 12f;
        if (frame.Info.WinnerLine.Length > 0)
        {
            var wsz = rec.Measure(frame.Info.WinnerLine, false, 1.2f);
            rec.ContentText("gameover.winner", "gameover", frame.Info.WinnerLine,
                new Vector2(panelX + (panelW - wsz.X) / 2f, cursorY), 1.2f);
            cursorY += rec.Measure("Mg", false, 1.2f).Y + 8f;
        }
        foreach (string line in frame.Info.ReasonLines)
        {
            var rsz = rec.Measure(line, false, 1f);
            rec.ContentText("gameover.reason", "gameover", line,
                new Vector2(panelX + (panelW - rsz.X) / 2f, cursorY), 1f);
            cursorY += rsz.Y + 6f;
        }
        rec.ContentPanel("gameover", frame.Panel);
    }
}
