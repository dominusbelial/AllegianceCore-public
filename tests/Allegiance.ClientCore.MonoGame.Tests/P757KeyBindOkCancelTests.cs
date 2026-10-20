using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P757 — native OK-versus-Cancel persistence for the in-game rebind popup
/// (<c>WinTrek/trekinput.cpp:2436-2458, 3005-3030</c>): <c>OnButtonOK</c> saves
/// the map then closes; <c>OnButtonCancel</c>/Esc just closes, and
/// <c>Close()</c> reloads the persisted map — so unsaved in-popup mutations are
/// discarded. The port's controller now mirrors that: mutations touch the live
/// map only, <see cref="KeyBindingController.Commit"/> persists,
/// <see cref="KeyBindingController.Close"/> reloads.
/// </summary>
public sealed class P757KeyBindOkCancelTests
{
    [Fact]
    public void Mutation_is_pending_until_commit_and_commit_persists_once()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        string saved = map.ToJson();
        int commits = 0, discards = 0;
        var ctl = new KeyBindingController(map,
            onCommit: () => { commits++; saved = map.ToJson(); },
            onDiscard: () => { discards++; map.ApplyJson(saved); });

        while (ctl.CurrentAction != "FireWeapon") ctl.MoveDown();
        ctl.SelectCurrent();
        ctl.CaptureKey(Keys.F15, false, false, false);
        // Live map changed (native's in-memory map is live too) but nothing saved.
        Assert.Equal(new KeyBinding(Keys.F15, InputModifiers.None), map.BindingOf("FireWeapon"));
        Assert.Equal(0, commits);

        ctl.Commit();   // OnButtonOK: SaveMap + Close (trekinput.cpp:2446-2450)
        Assert.Equal(1, commits);
        Assert.Equal(0, discards);
        Assert.Equal(KeyBindPhase.Closed, ctl.Phase);

        // The committed state survives a reload.
        map.ApplyJson(saved);
        Assert.Equal(new KeyBinding(Keys.F15, InputModifiers.None), map.BindingOf("FireWeapon"));
    }

    [Fact]
    public void Cancel_discards_pending_mutations_by_reloading_the_persisted_map()
    {
        // Native Close() reloads INPUTMAP_FILE (trekinput.cpp:2436-2444) — the
        // port's discard delegate is the store's Reload.
        var map = new ActionMap(InputActionCatalog.Definitions);
        string persisted = map.ToJson();
        var ctl = new KeyBindingController(map,
            onCommit: () => throw new InvalidOperationException("cancel must not save"),
            onDiscard: () => { map.ResetToDefaults(); map.ApplyJson(persisted); });

        while (ctl.CurrentAction != "ScreenShot") ctl.MoveDown();
        ctl.SelectCurrent();
        ctl.CaptureKey(Keys.X, false, false, false);
        Assert.Equal(KeyBindPhase.Conflict, ctl.Phase);
        ctl.Answer(true);   // swap applied to the live (pending) map
        Assert.Equal(new KeyBinding(Keys.X, InputModifiers.None), map.BindingOf("ScreenShot"));

        ctl.Close();        // OnButtonCancel (Esc): reload, discard
        Assert.Equal(KeyBindPhase.Closed, ctl.Phase);
        Assert.Equal(new KeyBinding(Keys.PrintScreen, InputModifiers.None), map.BindingOf("ScreenShot"));
        Assert.Equal(new KeyBinding(Keys.X, InputModifiers.None), map.BindingOf("ThrustDown"));
    }

    [Fact]
    public void Reset_is_also_pending_until_ok()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        map.SetBinding("FireWeapon", new KeyBinding(Keys.X, InputModifiers.None), force: true);
        string saved = map.ToJson();
        var ctl = new KeyBindingController(map, onDiscard: () => { map.ResetToDefaults(); map.ApplyJson(saved); });
        ctl.RequestReset();
        ctl.Answer(true);
        Assert.Equal(new KeyBinding(Keys.Space, InputModifiers.None), map.BindingOf("FireWeapon"));   // live map reset
        ctl.Close();
        Assert.Equal(new KeyBinding(Keys.X, InputModifiers.None), map.BindingOf("FireWeapon"));        // ... but not persisted
    }

    [Fact]
    public void Store_reload_restores_the_file_and_falls_back_to_defaults_without_saving()
    {
        // The Reload seam the discard delegate calls (native LoadMap(INPUTMAP_FILE)
        // else LoadMap(DEFAULTINPUTMAP_FILE), trekinput.cpp:2437-2441).
        string dir = Path.Combine(AppContext.BaseDirectory, "p757-keymap");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "keymap.json");

        var store = new KeyBindingStore();
        store.Map.SetBinding("FireWeapon", new KeyBinding(Keys.F9, InputModifiers.None), force: true);
        store.Map.SetBinding("ScreenShot", new KeyBinding(Keys.F8, InputModifiers.None), force: true);
        File.WriteAllText(path, store.Map.ToJson());

        var map2 = new KeyBindingStore();
        map2.Map.SetBinding("FireWeapon", new KeyBinding(Keys.F15, InputModifiers.None), force: true);
        map2.ReloadFrom(path);
        Assert.Equal(new KeyBinding(Keys.F9, InputModifiers.None), map2.Map.BindingOf("FireWeapon"));
        Assert.Equal(new KeyBinding(Keys.F8, InputModifiers.None), map2.Map.BindingOf("ScreenShot"));

        // A missing/unreadable file reverts to the catalogue defaults and does
        // NOT write anything (the file stays absent).
        string missing = Path.Combine(dir, "absent.json");
        map2.Map.SetBinding("FireWeapon", new KeyBinding(Keys.F15, InputModifiers.None), force: true);
        map2.ReloadFrom(missing);
        Assert.Equal(new KeyBinding(Keys.Space, InputModifiers.None), map2.Map.BindingOf("FireWeapon"));
        Assert.False(File.Exists(missing));
    }
}
