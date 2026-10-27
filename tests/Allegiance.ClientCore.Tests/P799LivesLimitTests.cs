using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.SinglePlayer;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P799 — the native lives limit, headless half. Native rules under test:
/// <list type="bullet">
/// <item><c>MissionParams::iLives</c> (<c>Igc/igc.h:1185</c>) defaults to the
/// unlimited sentinel <c>c_cUnlimitedLives = 0x7fff</c> (<c>igc.h:1007,1291</c>);
/// the port's <see cref="Gm.Lives"/> mirrors that default exactly, so every
/// pre-P799 config (no lives key) is byte-for-byte the old unlimited game.</item>
/// <item>A ship is a GHOST while <c>m_nDeaths &gt; iLives</c>
/// (<c>Igc/shipIGC.h:2465-2469</c>): N deaths with iLives = N−1 → ghost;
/// unlimited never ghosts.</item>
/// <item>The client learns iLives from MISSIONDEF(38) and its deaths from
/// PLAYERINFO(10)/KILL_SHIP(105) (<c>appmsg.cpp:2766-2773</c>,
/// <c>clintlib.h:347</c>) — exactly the values
/// <see cref="ClientWorld.OwnIsGhost"/> compares.</item>
/// <item>The death banner is gated by IsGhost ALONE
/// (<c>DeathBannerPane = SwitchPane(IsGhost, deathbannerbmp)</c>,
/// dialog.mdl:5438-5439; the value stamped at <c>WinTrek.cpp:3653</c>), not by
/// the transient Dead flag the port used before P799 — with unlimited lives no
/// death banner ever shows.</item>
/// <item>The lives badge shows <c>iLives − MissionDeaths()</c> only below 10
/// (<c>console.cpp:812-817</c>) — it appears exactly when the mission sends a
/// finite iLives.</item>
/// </list>
/// The live loopback half (server ghost gates, ghost relocation, side
/// elimination) is <see cref="P799LivesLimitLiveTests"/>.
/// </summary>
public sealed class P799LivesLimitTests
{
    // ── Config surface: default unlimited, real value on the wire ──────────

    [Fact]
    public void Game_config_lives_defaults_to_the_native_unlimited_sentinel()
    {
        // igc.h:1291 (MissionParams::Reset) + igc.h:1007 (c_cUnlimitedLives).
        Assert.Equal(MissionDef.UnlimitedLives, new Gm().Lives);
        Assert.Equal(unchecked((short)0x7fff), MissionDef.UnlimitedLives);
    }

    [Fact]
    public void Skirmish_lives_option_flows_into_the_game_config()
    {
        Assert.Equal(MissionDef.UnlimitedLives, new SkirmishOptions().CreateConfig().Games[0].Lives);
        Assert.Equal(1, new SkirmishOptions { Lives = 1 }.CreateConfig().Games[0].Lives);
        Assert.Equal(3, new SkirmishOptions { Lives = 3 }.CreateConfig().Games[0].Lives);
    }

    [Fact]
    public void Skirmish_lives_row_cycles_unlimited_then_finite_budgets()
    {
        var menu = new SkirmishScreenModel();
        menu.Move(8);                       // row 8 = the P799 Lives row
        Assert.Contains("Lives: Unlimited", menu.Rows);
        menu.Change(1);
        Assert.Equal(5, menu.Options.Lives);
        Assert.Contains("Lives: 5", menu.Rows);
        menu.Change(1);
        Assert.Equal(3, menu.Options.Lives);
        menu.Change(1);
        Assert.Equal(1, menu.Options.Lives);
        Assert.Contains("Lives: 1", menu.Rows);
        menu.Change(1);                     // wraps back to the sentinel
        Assert.Equal(MissionDef.UnlimitedLives, menu.Options.Lives);
        Assert.Equal(SkirmishScreenModel.RowCount, menu.Rows.Length);
        Assert.Equal(12, SkirmishScreenModel.StartRow);   // P814 Bot-gunners + P856 FF/Viz rows pushed these down
        Assert.Equal(13, SkirmishScreenModel.BackRow);
    }

    [Fact]
    public void Lives_choice_persists_through_the_player_config()
    {
        var pref = SkirmishPrefs.From(new SkirmishOptions { Lives = 1 });
        string path = Path.Combine(Path.GetTempPath(), "p799-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new Allegiance.ClientCore.MonoGame.IntroPreferences { Skirmish = pref }.Save(path);
            var restored = Allegiance.ClientCore.MonoGame.IntroPreferences.Load(path).Skirmish!;
            Assert.Equal(1, restored.Lives);
            Assert.Equal(1, restored.ToOptions().Lives);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missiondef_codec_round_trips_a_finite_lives_value()
    {
        // The wire slot the lives badge reads (console.cpp:812-817) must carry
        // the real budget, not the sentinel, when one is configured.
        var def = new MissionDef { SzIGCStaticFile = "PCore001", VerIgcCore = 7, ILives = 2 };
        var decoded = MissionDef.Decode(def.Encode());
        Assert.Equal(2, decoded.ILives);
        // ...and the default encode still carries the native sentinel.
        Assert.Equal(MissionDef.UnlimitedLives, MissionDef.Decode(new MissionDef().Encode()).ILives);
    }

    // ── Client ghost rule (the exact native comparison) ────────────────────

    private static ClientWorld WorldWithMe(short myId = 4400)
    {
        var w = new ClientWorld();
        w.Apply(new SetCluster
        {
            SectorId = 3,
            Ship = ServerShipUpdate.FromValues(
                myId, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 700u,
                Vector3.Zero, Orientation.Identity, Vector3.Zero,
                turnRates: new[] { 0f, 0f, 0f },
                hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
                energyFrac: 1f, oreFrac: 0f, power: 0f),
            Cookie = 0x11,
        });
        return w;
    }

    /// <summary>N deaths with iLives = N−1 → ghost; exactly N → not yet
    /// (native is a strict <c>&gt;</c>, shipIGC.h:2465-2469).</summary>
    [Theory]
    [InlineData(1, 0, true)]    // 1 death, iLives 0 → 1 > 0 → ghost (lives = 1 grants one life)
    [InlineData(2, 1, true)]    // 2 deaths, iLives 1 → ghost
    [InlineData(3, 2, true)]    // N deaths with iLives = N−1 → ghost
    [InlineData(1, 1, false)]   // 1 death, iLives 1 → not a ghost yet
    [InlineData(2, 2, false)]
    public void Own_ghost_rule_is_strictly_deaths_over_lives(short deaths, short lives, bool expectedGhost)
    {
        var w = WorldWithMe();
        w.Apply(new MissionDef { SzIGCStaticFile = "PCore001", VerIgcCore = 1, ILives = lives });
        w.Apply(new PlayerInfo { ShipId = w.MyShipId!.Value, ISide = 0, CharacterName = "me", NDeaths = deaths });
        Assert.Equal(lives, w.MissionLives);
        Assert.True(w.TryGetOwnMissionStats(out _, out short d));
        Assert.Equal(deaths, d);
        Assert.Equal(expectedGhost, w.OwnIsGhost);
    }

    [Fact]
    public void Unlimited_lives_never_ghosts()
    {
        var w = WorldWithMe();
        // No MISSIONDEF yet → the unlimited sentinel default.
        Assert.Equal(MissionDef.UnlimitedLives, w.MissionLives);
        w.Apply(new PlayerInfo { ShipId = w.MyShipId!.Value, ISide = 0, CharacterName = "me", NDeaths = 200 });
        Assert.False(w.OwnIsGhost, "0x7fff can never be exceeded — unlimited never ghosts (fsmission.cpp:4086)");
    }

    [Fact]
    public void No_playerinfo_yet_means_not_a_ghost()
    {
        var w = WorldWithMe();
        w.Apply(new MissionDef { SzIGCStaticFile = "PCore001", VerIgcCore = 1, ILives = 0 });
        Assert.False(w.OwnIsGhost, "no deaths known (no PLAYERINFO) — the native ppi null-check reads as not ghost");
    }

    // ── Death banner follows IsGhost, not Dead (headless half; the MonoGame
    // HudModel tests live in Allegiance.ClientCore.MonoGame.Tests) ──────────

    // ── Lives badge from the real value ────────────────────────────────────

    [Fact]
    public void Lives_badge_appears_only_for_a_finite_budget()
    {
        // console.cpp:812-817: iLives − MissionDeaths, badge on while < 10.
        Assert.True(HudConsoleFormat.LivesVisible(HudConsoleFormat.RemainingLives(3, 1)));
        Assert.False(HudConsoleFormat.LivesVisible(HudConsoleFormat.RemainingLives(MissionDef.UnlimitedLives, 0)),
            "the unlimited sentinel minus deaths is a huge number — no badge (the production behaviour)");
    }
}
