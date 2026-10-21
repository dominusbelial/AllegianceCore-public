using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P761 — the character sheet's profile-description editor (native
/// CharInfoScreen's Edit/Apply pair, WinTrek/CharInfoScreen.cpp:837-880): Edit
/// makes the description editable and swaps the buttons; Apply makes it
/// read-only, copies the text and swaps back. Native's club-server send
/// (CHARACTER_INFO_EDIT_DESCRIPTION) has no port channel, so the applied text
/// persists locally (client prefs) and the parity row stays partial — the
/// state toggles themselves are pinned here.
/// </summary>
public class P761PilotProfileTests
{
    [Fact]
    public void InitialState_ReadOnly_EditShown_ApplyHidden()
    {
        var e = new PilotInfoLayout.ProfileEditor { Description = "veteran" };
        var s = e.Snapshot;
        Assert.False(s.Editing);
        Assert.True(s.ReadOnly);
        Assert.True(s.EditVisible && s.EditEnabled);
        Assert.False(s.ApplyVisible || s.ApplyEnabled);
        Assert.Equal("veteran", e.Description);
    }

    [Fact]
    public void Edit_MakesItEditableAndFocused_HidesEdit_ShowsApply_DraftStartsEmpty()
    {
        // OnButtonEdit (CharInfoScreen.cpp:875-880): SetReadOnly(false),
        // SetHidden(false), SetValue("") — the draft starts EMPTY — Apply
        // enabled+visible, Edit disabled+hidden, focus to the edit pane.
        var e = new PilotInfoLayout.ProfileEditor { Description = "veteran" };
        e.BeginEdit();
        var s = e.Snapshot;
        Assert.True(s.Editing);
        Assert.False(s.ReadOnly);
        Assert.False(s.EditVisible || s.EditEnabled);
        Assert.True(s.ApplyVisible && s.ApplyEnabled);
        Assert.Equal("", e.Draft);
    }

    [Fact]
    public void Apply_MakesItReadOnlyAgain_CopiesTheDraft_AndSwapsTheButtonsBack()
    {
        // OnButtonApply (CharInfoScreen.cpp:839-858): SetReadOnly(true),
        // SetHidden(true), display string = edited text, Apply off, Edit back.
        var e = new PilotInfoLayout.ProfileEditor();
        e.BeginEdit();
        e.Append('f');
        e.Append('l');
        e.Append('y');
        string? applied = e.Apply();
        Assert.Equal("fly", applied);
        Assert.Equal("fly", e.Description);
        var s = e.Snapshot;
        Assert.False(s.Editing);
        Assert.True(s.ReadOnly);
        Assert.True(s.EditVisible && s.EditEnabled);
        Assert.False(s.ApplyVisible || s.ApplyEnabled);
    }

    [Fact]
    public void Apply_WhenNotEditing_IsANoop_ReturningNull()
    {
        // Native's Apply is disabled outside editing.
        var e = new PilotInfoLayout.ProfileEditor();
        Assert.Null(e.Apply());
    }

    [Fact]
    public void Cancel_LeavesTheStoredTextAndSwapsBack()
    {
        // The port's Esc path (native cancels by never pressing Apply).
        var e = new PilotInfoLayout.ProfileEditor { Description = "kept" };
        e.BeginEdit();
        e.Append('x');
        e.Cancel();
        Assert.False(e.Editing);
        Assert.Equal("kept", e.Description);
        Assert.Equal("", e.Draft);
    }

    [Fact]
    public void TypedText_OnlyLandsWhileEditing_AndIsCapped()
    {
        var e = new PilotInfoLayout.ProfileEditor();
        e.Append('z');
        Assert.Equal("", e.Draft);
        e.BeginEdit();
        for (int i = 0; i < PilotInfoLayout.ProfileEditor.MaxDescriptionLen + 10; i++)
            e.Append('a');
        Assert.Equal(PilotInfoLayout.ProfileEditor.MaxDescriptionLen, e.Draft.Length);
        e.Backspace();
        Assert.Equal(PilotInfoLayout.ProfileEditor.MaxDescriptionLen - 1, e.Draft.Length);
    }

    [Fact]
    public void AppliedText_PersistsInTheClientPrefs()
    {
        // The persistence half: the applied description lands in player.json
        // (the local stand-in for native's club-server
        // CHARACTER_INFO_EDIT_DESCRIPTION — no other player can see it, hence
        // the row stays partial).
        string path = Path.Combine(Path.GetTempPath(), "p761-profile-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var prefs = new IntroPreferences();
            prefs.PilotDescription = "flies PCore001";
            prefs.Save(path);
            var reloaded = IntroPreferences.Load(path);
            Assert.Equal("flies PCore001", reloaded.PilotDescription);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
