// P1042c — the in-game screen families in the screen-chrome proof: side select, ready check
// (roster / civ picker / team-name edit), mission briefing, team pane, quick chat, key
// bindings, connection lost, leaderboard, starmap + sector info, game over.
//
// Every frame is built by the PRODUCTION builder (PanelScreenFrames.* for the panel family;
// the renderer's own geometry for the leaderboard/starmap/game over), and the models are the
// real wire types (SideRoster, ScoreRosterEntry, SideEndgameInfo, GameOverPlayerEntry) or the
// real data files (quickchat.mdl, the default key map). Where a state cannot be reached
// without a live server the values are a fixture built from those real types; the report
// names each one.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.QuickChat;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Screens;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

sealed partial class ScreenProof
{
    private StarmapRenderer? _starmap;
    private SectorInfoPaneRenderer? _sectorInfo;
    private ScoreboardOverlayRenderer? _scoreboard;
    private GameOverScreenRenderer? _gameOver;
    private SideSelectOverlayRenderer? _sideSelect;
    private ReadyCheckOverlayRenderer? _readyCheck;
    private MissionBriefingRenderer? _briefing;
    private TeamPaneRenderer? _teamPane;
    private QuickChatMenuRenderer? _quickChat;
    private KeyBindingRenderer? _keyBind;
    private KeyBindingRenderer? _keyBindCapture;
    private ConnectionLostScreenRenderer? _connectionLost;
    private PanelScreenPainter? _panelPainter;
    private QuickChatDatabase? _quickChatDb;
    private KeyBindingController? _keyBindCtl;
    private SideRoster _roster = null!;
    private IReadOnlyList<ScoreboardLayout.Row> _scoreRows = null!;
    private IReadOnlyList<StarmapLayout.ClusterSite> _sites = null!;
    private IReadOnlyList<ClusterInfo.WarpEdge> _edges = null!;
    private SectorInfoPaneLayout.Data _infoPane = null!;
    private GameOverScreenRenderer.Tables _gameTables = null!;
    private GameOverPanelInfo _gameInfo;

    /// <summary>Build the in-game fixtures from real model types (no faked drawing).</summary>
    private void LoadP1042cScenes()
    {
        // Side roster: real SIDE_ROSTER record values (three sides + a 2-player lobby).
        _roster = new SideRoster
        {
            LobbyPlayerCount = 2,
            Sides =
            [
                new(0, 0, 3, "Iron Coalition", 0.9f, 0.4f, 0.3f, 1f),
                new(1, 1, 2, "Belters", 0.4f, 0.7f, 0.9f, 1f),
                new(2, 2, 1, "Gigacorp", 0.6f, 0.9f, 0.5f, 1f),
            ],
        };

        // Leaderboard: real ScoreRosterEntry rows + the server rank table shape.
        var entries = new List<ScoreRosterEntry>
        {
            new("IronWolf", 0, 12, 4, 1, 1),
            new("BeltRunner", 0, 7, 6, 0, 2),
            new("QuietRock", 1, 9, 3, 2, 1),
            new("GigaPilot", 1, 2, 8, 3, 0),
            new("Nova", 2, 5, 5, 0, 3),
        };
        var ranks = new List<RankInfoEntry>
        {
            new(0, -1, 1, "Novice 1"), new(5, -1, 2, "Novice 3"), new(20, -1, 3, "Veteran 2"),
            new(50, -1, 4, "Veteran 4"),
        };
        _scoreRows = ScoreboardLayout.Build(entries, ranks);

        // Starmap: authored cluster coordinates (DataClusterIGC.ScreenX/ScreenY values).
        _sites =
        [
            new(0, "Sol", 0f, 0f), new(1, "Cortex", 180f, 60f), new(2, "Alphara", -160f, 90f),
            new(3, "Dregs", 60f, -140f), new(4, "Serenity", -90f, -120f),
        ];
        _edges =
        [
            new(0, 1), new(0, 2), new(1, 3), new(2, 4), new(3, 4),
        ];
        var samples = new List<SectorInfoPaneLayout.AsteroidSample>
        {
            new(0, 1400f), new(1, 260f), new(0, 800f), new(2, 120f),
        };
        _infoPane = SectorInfoPaneLayout.Build("Sol", samples, 3);

        // Game over: the real GAME_OVER(62) record shapes.
        var go = new GameOver
        {
            NumSides = 2,
            SideWinner = 0,
            EjectPods = false,
            Sides =
            [
                new("Iron Coalition", 0, 0.9f, 0.4f, 0.3f, 1f, 34, 12, 3, 12, 4, 2, 1),
                new("Belters", 1, 0.4f, 0.7f, 0.9f, 1f, 22, 26, 5, 8, 1, 1, 0),
                default, default, default, default,
            ],
        };
        _gameInfo = GameOverPanelInfo.From(go, 0);
        var players = new List<GameOverPlayerEntry>
        {
            new("IronWolf", 0, 3, 120f, 12, 4, 1, 0, 5, 2, 1, 0, 2, 1830f),
            new("BeltRunner", 0, 2, 80f, 7, 6, 0, 1, 3, 1, 0, 0, 1, 1520f),
            new("QuietRock", 1, 3, 95f, 9, 3, 2, 0, 4, 0, 0, 1, 0, 1640f),
            new("GigaPilot", 1, 1, 20f, 2, 8, 3, 0, 2, 0, 0, 0, 0, 900f),
        };
        var commanders = new HashSet<string>(StringComparer.Ordinal) { "IronWolf", "QuietRock" };
        var sideColors = new Dictionary<short, Color>
        {
            [0] = new(0.9f, 0.4f, 0.3f), [1] = new(0.4f, 0.7f, 0.9f),
        };
        _gameTables = new GameOverScreenRenderer.Tables(
            GameOverTablesLayout.BuildSides(go.Sides, go.NumSides, new(GameOverTablesLayout.SideColumn.Kills, false)),
            GameOverTablesLayout.BuildPlayers(players, rank => RankNameLookup.Lookup(ranks, rank),
                sideId => sideId >= 0 && sideId < go.Sides.Length ? go.Sides[sideId].CivId : (short)-1,
                commanders, ejectPods: false,
                new GameOverTablesLayout.PlayerSort(GameOverTablesLayout.PlayerColumn.Kills, false)),
            new(GameOverTablesLayout.SideColumn.Kills, false),
            new(GameOverTablesLayout.PlayerColumn.Kills, false),
            sideColors);

        // Quick chat: the real quickchat.mdl/sounddef.mdl from the artwork corpus.
        try
        {
            string art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
            string qc = Path.Combine(art, "quickchat.mdl"), sd = Path.Combine(art, "sounddef.mdl");
            if (File.Exists(qc) && File.Exists(sd))
                _quickChatDb = QuickChatMdlParser.Parse(File.ReadAllText(qc), File.ReadAllText(sd));
        }
        catch (Exception e) { Console.WriteLine("P1042C_QUICKCHAT_SKIP " + e.Message); }

        // Key bindings: the live default map (real actions, labels and descriptions).
        _keyBindCtl = new KeyBindingController(KeyBindingStore.Instance.Map);
    }

    // ── model fixtures ───────────────────────────────────────────────────────
    private IReadOnlyList<SideSelectLayout.Row> SideRows(bool withRoster)
    {
        if (!withRoster)
            return SideSelectLayout.Build(null, 0, amMissionOwner: true);
        var lobby = new List<SideSelectLayout.LobbyMember>
        {
            new("BeltRunner", false, true), new("Nova", true, false),
        };
        return SideSelectLayout.Build(_roster, 1, amMissionOwner: true, isSideActive: null,
            civNameFor: id => id switch { 0 => "Iron Coalition", 1 => "Belters", 2 => "Gigacorp", _ => null },
            lobbyMembers: lobby, amInTeamLobby: false, ownName: "Nova", ownRequestPending: false,
            livePlayerCount: id => id switch { 0 => 3, 1 => 2, 2 => 1, _ => 0 });
    }

    private IReadOnlyList<ReadyCheckLayout.Row> ReadyRows(int state)
    {
        var players = new List<ReadyCheckLayout.PlayerRow>
        {
            new("IronWolf", true, false), new("BeltRunner", false, false), new("Nova", false, true),
        };
        var rows = ReadyCheckLayout.Build("Iron Coalition", ownReady: false, players,
            sideAggregateReady: false, forcedReady: false, canForceTeamReady: true,
            launchCountdown: 7,
            lobby: [new("GigaPilot", false, "Iron Coalition"), new("QuietRock", false, null)],
            canAcceptLobbyRequest: true, amMissionOwner: true).ToList();
        // The civ-picker and team-name edit states need a team-leader lobby world (mission
        // core + own ship + side), which a static proof cannot build without a live server;
        // these rows are the model's own strings in the model's own row kinds (fixture).
        if (state >= 1)
        {
            rows.Add(ReadyCheckLayout.Row.Blank());
            rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Title, "SELECT CIVILIZATION"));
            rows.Add(new ReadyCheckLayout.Row(ReadyCheckLayout.Kind.Item, "  Iron Coalition"));
            rows.Add(new ReadyCheckLayout.Row(ReadyCheckLayout.Kind.Item, "> Belters", Highlight: true));
            rows.Add(new ReadyCheckLayout.Row(ReadyCheckLayout.Kind.Item, "  Gigacorp"));
            rows.Add(new ReadyCheckLayout.Row(ReadyCheckLayout.Kind.Item, "  RANDOM"));
            rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Hint,
                "UP/DOWN SELECT   ENTER CONFIRM   C CLOSE"));
        }
        if (state >= 2)
        {
            rows.Add(new ReadyCheckLayout.Row(ReadyCheckLayout.Kind.Item,
                "TEAM NAME: Iron Coaltion_", Highlight: true));
            rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Hint,
                $"TYPE A NAME (MAX {TeamNameEdit.MaxLength})   ENTER APPLY   ESC CANCEL   BACKSPACE DELETE"));
            rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Error,
                "Name already taken on this mission."));
        }
        return rows;
    }

    private static readonly IReadOnlyList<ScoreRosterEntry> _compactEntries =
    [
        new("IronWolf", 0, 12, 4, 1, 3), new("BeltRunner", 0, 7, 6, 0, 2),
        new("Nova", 0, 0, 0, 0, 1),
    ];

    private static IReadOnlyList<TeamPaneLayout.Line> TeamLines(bool compact)
    {
        var lines = new List<TeamPaneLayout.Line>
        {
            new(TeamPaneLayout.LineKind.Title, "TEAM ROSTER"),
            new(TeamPaneLayout.LineKind.SideHeader, "IRON COALITION  (3 pilots)"),
            new(TeamPaneLayout.LineKind.Pilot, "  IronWolf       Veteran 2   FLYING   K 12  D 4", true),
            new(TeamPaneLayout.LineKind.Pilot, "  BeltRunner     Novice 3    FLYING   K  7  D 6"),
            new(TeamPaneLayout.LineKind.Pilot, "  Nova           Novice 1    DOCKED   K  0  D 0"),
            new(TeamPaneLayout.LineKind.SideHeader, "BELTERS  (2 pilots)"),
            new(TeamPaneLayout.LineKind.Pilot, "  QuietRock      Veteran 2   FLYING   K  9  D 3"),
            new(TeamPaneLayout.LineKind.Blank, ""),
            new(TeamPaneLayout.LineKind.Money, "MONEY  $1,250"),
            new(TeamPaneLayout.LineKind.Ballot, "BALLOT  next base: Cortex"),
            new(TeamPaneLayout.LineKind.Hint, "T TOGGLE | N SELECT | S SORT | G GOTO/ACCEPT"),
        };
        return compact ? TeamPaneLayout.Compact(lines, _compactEntries, 0) : lines;
    }

    // ── frames ───────────────────────────────────────────────────────────────
    private ScreenPanelFrame PanelFrame(string family, int state, int w, int h)
    {
        var vp = new Viewport(0, 0, w, h);
        switch (family)
        {
            case "sideselect":
                return PanelScreenFrames.SideSelectFrame(SideRows(state == 0), vp, _small, _big);
            case "readycheck":
                return PanelScreenFrames.ReadyCheckFrame(ReadyRows(state), vp, _small, _big, null);
            case "briefing":
                return PanelScreenFrames.BriefingFrame(
                    "Conquest of Sol", state == 0
                        ? "Destroy the enemy command ship in Sol.\n\nOther objectives include:\n * Control the helium rock in Cortex\n * Defend the outpost in Alphara\n\nGood hunting, pilot."
                        : "The server is preparing the mission. Stand by.",
                    state == 0 ? 12 : null, "Tactical Briefing", vp, _small, _big);
            case "teampane":
                return PanelScreenFrames.TeamPaneFrame(TeamLines(state == 1), vp, _small, _big, state == 1);
            case "quickchat":
            {
                var root = _quickChatDb?.MainMenu;
                if (root is null) throw new InvalidOperationException("quickchat.mdl not parsed");
                var trail = new List<QuickChatMenu> { root };
                if (state == 1)
                {
                    QuickChatMenu? sub = null;
                    for (int i = 0; i < root.Choices.Count; i++)
                        if (root.Choices[i].Submenu is { } child) { sub = child; break; }
                    if (sub is not null) trail.Add(sub);
                }
                return PanelScreenFrames.QuickChatFrame(trail[^1], trail, vp, _small);
            }
            case "keybind":
            {
                var ctl = new KeyBindingController(KeyBindingStore.Instance.Map);
                if (state == 1) ctl.SelectCurrent();
                return PanelScreenFrames.KeyBindFrame(ctl, vp, _small);
            }
            default:
                return PanelScreenFrames.ConnectionLostFrame(vp, _small, _big);
        }
    }

    private ScoreboardFrame ScoreFrame(int w, int h)
    {
        var vp = new Viewport(0, 0, w, h);
        var geo = ScoreboardLayout.Layout(_scoreRows, s => _small.MeasureString(s).X, w, h);
        var bands = new RectangleF[_scoreRows.Count];
        float y = geo.HeaderY + geo.RowHeight;
        for (int i = 0; i < bands.Length; i++)
        {
            bands[i] = new RectangleF(geo.Panel.X + 8f, y, geo.Panel.Width - 16f, geo.RowHeight);
            y += geo.RowHeight;
        }
        return new ScoreboardFrame(vp, _scoreRows, "Nova",
            new RectangleF(geo.Panel.X, geo.Panel.Y, geo.Panel.Width, geo.Panel.Height),
            new RectangleF(geo.Panel.X + 8f, geo.HeaderY, geo.Panel.Width - 16f, geo.RowHeight),
            bands, null, new ScreenInks(Color.White, Color.White, Color.Gray, Color.Orange));
    }

    private StarmapScreenFrame StarmapFrame(int w, int h)
    {
        var vp = new Viewport(0, 0, w, h);
        float panelW = MathF.Min(w * 0.80f, 1040f);
        float panelH = MathF.Min(h * 0.66f, 640f);
        float panelX = (w - panelW) / 2f;
        float panelY = (h - panelH) / 2f;
        var geo = SectorInfoPaneRenderer.Box(_infoPane, panelX, panelY, panelW, panelH,
            _small.LineSpacing, s => _small.MeasureString(s).X);
        var btn = SectorInfoPaneRenderer.ExpandButtonRect(geo.Box.X, geo.Box.Y, geo.Box.Width, geo.Box.Height);
        return new StarmapScreenFrame(vp, _sites, _edges, 0, 1,
            id => id == 1 ? (short)2 : (short)0, true,
            panelW, panelH, null, id => id == 0 ? new Color(0.9f, 0.4f, 0.3f) : null,
            null, null, null,
            _infoPane, new Color(0.9f, 0.4f, 0.3f), true,
            new RectangleF(panelX, panelY, panelW, panelH), geo.Box,
            new RectangleF(btn.X, btn.Y, btn.Width, btn.Height),
            new ScreenInks(Color.White, Color.White, Color.Gray, Color.Orange));
    }

    private GameOverScreenFrame GameOverFrame(int w, int h)
    {
        var vp = new Viewport(0, 0, w, h);
        float panelW = MathF.Min(w * 0.94f, 1240f), panelH = MathF.Min(h * 0.94f, 760f);
        return new GameOverScreenFrame(vp, _gameInfo, _gameTables, null, null,
            new RectangleF((w - panelW) / 2f, (h - panelH) / 2f, panelW, panelH),
            new ScreenInks(Color.White, Color.White, Color.Gray, Color.Orange));
    }

    /// <summary>Draw one P1042c scene through the given skin column.</summary>
    private bool DrawP1042cScene(IScreenSkinRenderer screen, Scene scene, int w, int h)
    {
        switch (scene.Family)
        {
            case "sideselect":
            case "readycheck":
            case "briefing":
            case "teampane":
            case "quickchat":
            case "keybind":
            case "connectionlost":
                screen.DrawPanel(PanelFrame(scene.Family, scene.State, w, h));
                return true;
            case "scoreboard":
                screen.DrawScoreboard(ScoreFrame(w, h));
                return true;
            case "starmap":
                screen.DrawStarmap(StarmapFrame(w, h));
                return true;
            case "gameover":
                screen.DrawGameOver(GameOverFrame(w, h));
                return true;
        }
        return false;
    }

    /// <summary>Draw a P1042c scene through the PRE-BOUNDARY renderers (the legacy golden).</summary>
    private bool DrawLegacyScene(Scene scene, int w, int h, HudStyle style)
    {
        var vp = new Viewport(0, 0, w, h);
        switch (scene.Family)
        {
            case "sideselect": _sideSelect!.Draw(vp, SideRows(scene.State == 0)); return true;
            case "readycheck": _readyCheck!.Draw(vp, ReadyRows(scene.State), null); return true;
            case "briefing":
                if (scene.State == 0)
                    _briefing!.Draw(vp, "Conquest of Sol",
                        "Destroy the enemy command ship in Sol.\n\nOther objectives include:\n * Control the helium rock in Cortex\n * Defend the outpost in Alphara\n\nGood hunting, pilot.",
                        12, "Tactical Briefing");
                else
                    _briefing!.Draw(vp, "Conquest of Sol", "The server is preparing the mission. Stand by.", null,
                        "Tactical Briefing");
                return true;
            case "teampane": _teamPane!.Draw(vp, TeamLines(scene.State == 1), scene.State == 1); return true;
            case "quickchat":
            {
                var frame = PanelFrame("quickchat", scene.State, w, h);
                var root = _quickChatDb!.MainMenu!;
                var trail = new List<QuickChatMenu> { root };
                if (scene.State == 1)
                {
                    for (int i = 0; i < root.Choices.Count; i++)
                        if (root.Choices[i].Submenu is { } sub) { trail.Add(sub); break; }
                }
                _quickChat!.Draw(trail[^1], trail, vp);
                return true;
            }
            case "keybind":
                (scene.State == 1 ? _keyBindCapture! : _keyBind!).Draw(vp);
                return true;
            case "connectionlost": _connectionLost!.Draw(vp); return true;
            case "scoreboard":
                _scoreboard!.Draw(vp, _scoreRows, "Nova");
                return true;
            case "starmap":
            {
                var frame = StarmapFrame(w, h);
                _starmap!.Draw(frame.Sites, frame.Edges, frame.CurrentSector, frame.SelectedSector,
                    frame.Warnings, frame.FlashOn, vp, frame.PanelW, frame.PanelH, frame.Owners,
                    frame.SideColor, frame.KnownEnemySectors, frame.HighlightedClusters, frame.ChatCluster);
                _sectorInfo!.Draw(_infoPane, vp, frame.Panel.X, frame.Panel.Y, frame.Panel.Width,
                    frame.Panel.Height, frame.OwnSideColor, frame.Expanded);
                return true;
            }
            case "gameover":
                _gameOver!.Draw(vp, _gameInfo, _gameTables, null, null);
                return true;
        }
        return false;
    }
}
