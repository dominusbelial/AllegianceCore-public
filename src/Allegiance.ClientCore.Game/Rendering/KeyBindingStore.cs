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
        Map = new ActionMap(InputActionCatalog.Definitions);
        string path = ResolvePath();
        if (File.Exists(path))
        {
            try
            {
                Map.ApplyJson(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[keymap] failed to read {path}: {ex.Message}");
            }
        }
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
            try
            {
                // Native LoadMap builds the map from the defaults + the file's
                // overrides; applying the JSON to the mutated map alone would
                // leave removed overrides behind, so reset first.
                Map.ResetToDefaults();
                Map.ApplyJson(File.ReadAllText(path));
                Console.WriteLine($"[keymap] reloaded bindings <- {path}");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[keymap] failed to read {path}: {ex.Message} - falling back to defaults");
            }
        }
        Map.ResetToDefaults();
        Console.WriteLine("[keymap] no readable keymap - reverted to catalogue defaults (not saved)");
    }
}
