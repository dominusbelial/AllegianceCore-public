using System;
using System.IO;
using Allegiance.ClientCore.MonoGame.Chat;
using Allegiance.ClientCore.MonoGame.Games;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P871 — the client half of the general-matrix rows: the new-game wizard's
/// selection/validation model (native <c>NewGameScreen</c> +
/// <c>CREATE_MISSION_REQ</c>), the directory-chat window (native chat relay and
/// mute/echo semantics) and the installed file-set identity
/// (<c>LOGONREQ.crcFileList</c>).
/// </summary>
public class P871WizardChatAndFileListTests
{
    private static MissionParamsMsg ValidParams() => new()
    {
        StrGameName = "Wizard",
        SzIgcStaticFile = "PCore001.igc",
        NTems = 2,
        NTotalMaxPlayersPerGame = 32,
        NMaxPlayersPerTeam = 16,
        IGoalConquestPercentage = 100,
        IGoalTerritoryPercentage = 100,
        Bits = { [MissionParamsMsg.BitObjectModelCreated] = true, [MissionParamsMsg.BitScoresCount] = true },
    };

    private static CreateGameWizard.Catalog Catalog() => new(
        [new CreateGameWizard.ServerChoice("s1", "Host", "127.0.0.1", 6073, "PCore001.igc", "Mission")],
        ["PCore001.igc", "PCore014.igc"],
        ["XXV_simple.igc", "Brawl.igc"]);

    [Fact]
    public void The_wizard_selects_a_server_its_core_and_a_map_then_creates_the_request()
    {
        var wizard = new CreateGameWizard();
        wizard.ApplyCatalog(Catalog());
        Assert.Equal("Host", wizard.SelectedServer!.Name);
        Assert.Equal("PCore001.igc", wizard.SelectedCore);
        Assert.Equal("XXV_simple.igc", wizard.SelectedMap);
        // Type a name (native's edit control) and create.
        wizard.TypeName("My Game");
        Assert.Equal("My Game", wizard.GameName);
        wizard.Backspace();
        Assert.Equal("My Gam", wizard.GameName);
        wizard.TypeName("e");
        var request = wizard.TryCreate();
        Assert.True(request is not null, wizard.Status);
        Assert.Equal("s1", request!.ServerId);
        Assert.Equal("PCore001.igc", request.Core);
        Assert.Equal("XXV_simple.igc", request.Map);
        Assert.Equal("My Game", request.GameName);
        Assert.Equal("My Game", request.Params.StrGameName);
        Assert.Null(request.Params.Invalid(bFromClient: true));
    }

    [Fact]
    public void The_wizard_refuses_an_empty_name_a_foreign_core_and_invalid_params()
    {
        var wizard = new CreateGameWizard();
        wizard.ApplyCatalog(Catalog());
        Assert.Null(wizard.TryCreate());                       // no name yet
        Assert.Equal("Type a game name first.", wizard.Status);

        wizard.TypeName("Name");
        wizard.Move(1);                                        // CORE row
        wizard.Adjust(1);                                      // PCore014
        Assert.Equal("PCore014.igc", wizard.SelectedCore);
        Assert.Null(wizard.TryCreate());                       // not the server's core
        Assert.Contains("core", wizard.Status, StringComparison.OrdinalIgnoreCase);
        wizard.Adjust(-1);

        var invalid = ValidParams();
        invalid.NTems = 1;                                     // Invalid(true) requires 2..6
        wizard.Params = invalid;
        Assert.Null(wizard.TryCreate());
        Assert.False(string.IsNullOrEmpty(wizard.Status));
    }

    [Fact]
    public void The_wizard_marks_the_lobbys_answer_on_its_status_line()
    {
        var wizard = new CreateGameWizard();
        wizard.Acknowledge("game7", null);
        Assert.Contains("game7", wizard.Status);
        wizard.Acknowledge(null, "Sign in to create a game.");
        Assert.Equal("Sign in to create a game.", wizard.Status);
    }

    // ── directory chat window ──────────────────────────────────────────────

    private static DirectoryChatLine Line(long seq, string senderId, string sender, string text) =>
        new(seq, "directory", senderId, sender, text, DateTimeOffset.UnixEpoch.AddSeconds(seq));

    [Fact]
    public void The_directory_chat_window_advances_its_cursor_and_never_echoes_the_sender()
    {
        var log = new DirectoryChatLog();
        var added = log.Accept([Line(1, "me", "Me", "mine"), Line(2, "you", "You", "yours")], ownAccountId: "me");
        Assert.Equal("yours", Assert.Single(added).Text);
        Assert.Equal(2, log.Cursor);

        // Polling again with the same batch adds nothing (the cursor dedupes).
        Assert.Empty(log.Accept([Line(1, "me", "Me", "mine"), Line(2, "you", "You", "yours")], "me"));

        // A history rebuild (cursor 0) keeps the reader's own lines.
        var history = new DirectoryChatLog();
        history.Accept([Line(1, "me", "Me", "mine")], ownAccountId: "me", historyRebuild: true);
        Assert.Single(history.Lines);
    }

    [Fact]
    public void The_directory_chat_window_drops_muted_senders_and_formats_like_the_pane()
    {
        var log = new DirectoryChatLog();
        log.Mute("spammer");
        log.Accept([Line(1, "spammer", "Spammer", "buy gold"), Line(2, "friend", "Friend", "hi")], null);
        Assert.Equal("hi", Assert.Single(log.Lines).Text);
        Assert.True(log.IsMuted("spammer"));
        log.Unmute("spammer");
        log.Accept([Line(3, "spammer", "Spammer", "sorry")], null);
        Assert.Equal(2, log.Lines.Count);
        // The formatted line follows the game chat log convention (sender: text).
        Assert.Contains("Spammer", DirectoryChatLog.Format(log.Lines[^1]));
        Assert.Contains("sorry", DirectoryChatLog.Format(log.Lines[^1]));

        // The bounded log drops the oldest entries.
        var ring = new DirectoryChatLog();
        ring.Accept(Enumerable.Range(1, DirectoryChatLog.MaxLines + 10)
            .Select(i => Line(i, "s", "S", "line" + i)), null);
        Assert.Equal(DirectoryChatLog.MaxLines, ring.Lines.Count);
        Assert.Equal("line" + (DirectoryChatLog.MaxLines + 10), ring.Lines[^1].Text);
    }

    // ── installed file-set identity ────────────────────────────────────────

    [Fact]
    public void The_client_reports_the_crc_of_its_installed_file_list()
    {
        string dir = Path.Combine(Path.GetTempPath(), "p871-clientlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ClientFileList.ResetCache();
            // Nothing installed: native's "checks disabled" value.
            Assert.Equal(FileListCrc.Unknown, ClientFileList.Compute(dir));
            File.WriteAllText(Path.Combine(dir, "artwork-manifest.txt"), "art/a.mdl\nart/b.mdl\n");
            int expected = FileListCrc.ComputeFile(Path.Combine(dir, "artwork-manifest.txt"));
            Assert.Equal(expected, ClientFileList.Compute(dir));
            Assert.EndsWith("artwork-manifest.txt", ClientFileList.ResolvedPath);
            // The native FileList.txt name is the fallback.
            File.Delete(Path.Combine(dir, "artwork-manifest.txt"));
            File.WriteAllText(Path.Combine(dir, "FileList.txt"), "PCore001.igc\n");
            Assert.Equal(FileListCrc.ComputeFile(Path.Combine(dir, "FileList.txt")), ClientFileList.Compute(dir));
        }
        finally
        {
            ClientFileList.ResetCache();
            Directory.Delete(dir, recursive: true);
        }
    }
}
