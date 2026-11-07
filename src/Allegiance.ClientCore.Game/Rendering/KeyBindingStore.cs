using System;
using System.IO;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P625 - process-wide owner of the live <see cref="ActionMap"/> and its JSON
/// persistence (a user keymap file beside the other client settings, i.e. the
/// per-user configuration directory; overridable via
/// <c>ALLEGIANCE_KEYMAP</c> for tests / isolated runs). Game1 and the rebind +
/// help renderers all read and mutate the SAME instance so a rebind is
/// reflected in the help overlay instantly (the P625 requirement).
/// </summary>
public sealed class KeyBindingStore
{
    /// <summary>Per-user keymap file name (stored next to the other client
    /// settings in the per-user configuration directory).</summary>
    public const string FileName = "keymap.json";

    private static KeyBindingStore? _instance;
    public static KeyBindingStore Instance => _instance ??= new KeyBindingStore();

    public ActionMap Map { get; }

    /// <summary>P949 — true when the store found a keymap file at construction
    /// (a returning player). The P949 keys hint is for a fresh profile only.</summary>
    public bool LoadedFromFile { get; private set; }

    /// <summary>P949 — set by <see cref="Save"/>: once the player changes a
    /// binding (or presses OK in F6) the profile is no longer fresh.</summary>
    public bool SavedThisSession { get; private set; }

    /// <summary>P949 — a profile has a user keymap (it was there at load or has
    /// been written since). Fresh profiles have none: nothing has been persisted
    /// yet, and the layout fallback is deliberately NOT written (native's
    /// <c>LoadMap(INPUTMAP_FILE)</c> fallback to the default map writes nothing
    /// either, trekinput.cpp:125-126,2439-2441).</summary>
    public bool HasKeymap => LoadedFromFile || SavedThisSession;

    /// <summary>P949 — a profile with no readable keymap file. The fresh-profile
    /// keys hint shows once for it.</summary>
    public bool FreshProfile => !HasKeymap;

    /// <summary>Absolute path of the keymap file in the user config or explicit directory
    /// (honours ALLEGIANCE_KEYMAP for tests/isolation).</summary>
    public static string ResolvePath(string? workingDir = null)
    {
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_KEYMAP") is { Length: > 0 } over)
            return over;
        string dir = workingDir ?? PlayerConfig.DirectoryPath;
        return Path.Combine(dir, FileName);
    }

    public KeyBindingStore()
    {
        // P949 — a fresh profile (no keymap file) starts on the Port layout
        // (keyboard steering, the refreshed-install complaint this fixes); a file
        // that records a preset keeps that preset, and a pre-P941 flat file (no
        // layout marker) loads as the Port layout its untouched chords came from.
        // The fallback is not written: the file stays absent until the player
        // changes something (native's LoadMap fallback writes nothing).
        Map = new ActionMap(InputActionCatalog.Definitions);
        string path = ResolvePath();
        if (File.Exists(path))
        {
            LoadedFromFile = true;
            try
            {
                Map.ApplyJson(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[keymap] failed to read {path}: {ex.Message}");
            }
        }
        Console.WriteLine($"[keymap] layout preset: {InputLayouts.Name(Map.Layout)}"
            + (FreshProfile ? " (fresh profile, not saved)" : ""));
    }

    /// <summary>P941 — the F6 preset cycle. Switches the whole default map and
    /// persists (native's <c>SaveMap</c> writes the current map, trekinput.cpp:2376-2384).</summary>
    public void CycleLayout()
    {
        Map.SetLayout(InputLayouts.Next(Map.Layout));
        Save();
    }

    /// <summary>Persist the current (user-overridden) bindings to the keymap
    /// file. Only differences from defaults are written.</summary>
    public void Save()
    {
        string path = ResolvePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, Map.ToJson());
            SavedThisSession = true;
            Console.WriteLine($"[keymap] saved bindings -> {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[keymap] failed to save {path}: {ex.Message}");
        }
    }

    /// <summary>Restore every binding to the catalogue default and persist.</summary>
    public void ResetToDefaults()
    {
        Map.ResetToDefaults();
        Save();
    }

    /// <summary>Apply a binding, persisting it. Returns the conflicting action
    /// name when the key is taken and <paramref name="force"/> was false.</summary>
    public string? Bind(string action, KeyBinding chord, bool force = false)
    {
        string? conflict = Map.SetBinding(action, chord, force);
        Save();
        return conflict;
    }

    /// <summary>Rebind <paramref name="a"/> to <paramref name="b"/>'s chord and
    /// vice versa (native's swap offer), then persist.</summary>
    public void Swap(string a, string b)
    {
        Map.SwapBindings(a, b);
        Save();
    }

    /// <summary>P757 — native <c>TrekInput::Close()</c> (trekinput.cpp:2436-2444):
    /// reload the persisted keymap, falling back to the catalogue defaults when
    /// no file is readable (<c>LoadMap(INPUTMAP_FILE)</c>, else
    /// <c>LoadMap(DEFAULTINPUTMAP_FILE)</c>). This is the popup's Cancel path —
    /// it discards every unsaved in-popup mutation. Deliberately does NOT
    /// persist the fallback (the file stays as it was).</summary>
    public void Reload() => ReloadFrom(ResolvePath());

    /// <summary>The reload body with an explicit path (the test seam behind
    /// <see cref="Reload"/>, which always passes the resolved user path).</summary>
    internal void ReloadFrom(string path)
    {
        if (File.Exists(path))
        {
            LoadedFromFile = true;
            try
            {
                // Native LoadMap builds the map from the defaults + the file's
                // overrides; applying the JSON to the mutated map alone would
                // leave removed overrides behind, so reset first.
                Map.ResetToDefaults();
                Map.ApplyJson(File.ReadAllText(path));
                Console.WriteLine($"[keymap] reloaded bindings <- {path} ({InputLayouts.Name(Map.Layout)})");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[keymap] failed to read {path}: {ex.Message} - falling back to defaults");
            }
        }
        // P949 — native falls back to LoadMap(DEFAULTINPUTMAP_FILE). The port's
        // fresh-profile default is the Port layout (P949), so that is the
        // fallback the Cancel path restores; it is NOT persisted, exactly as
        // before P941 (the file stays as it was).
        Map.SetLayout(InputLayout.Port);
        Console.WriteLine("[keymap] no readable keymap - reverted to the Port default layout (not saved)");
    }
}
