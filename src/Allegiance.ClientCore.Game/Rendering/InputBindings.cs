using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P625 - the pure data model for the rebindable key map.
///
/// Native holds a hard-coded default map (<c>defaultinputmap.mdl</c>) plus an
/// in-game rebinding popup (<c>InputMapPopup</c>, WinTrek/trekinput.cpp:1631+).
/// The port previously shipped a hard-coded, non-configurable
/// <c>KeyBindingTable</c> (P257) that drifted from the code (P617 flags "cannot
/// rebind keys"). This file introduces the data-driven action map: every action
/// has a native TK name (from <c>trekinput.cpp:1459</c>'s command-name table),
/// a category (mirroring native's command categories), a description, an
/// effective default binding and its native default (from
/// <c>defaultinputmap.mdl</c>), and a current (possibly user-overridden)
/// binding. All input handlers resolve through <see cref="ActionMap"/>, never a
/// raw key literal - the modern "data-driven action map" approach the P625
/// brief calls for.
///
/// Purity: <see cref="KeyBinding"/> uses the MonoGame <see cref="Keys"/> enum
/// purely as a portable key identifier (it is an enum, needs no device), so
/// this file is headless-unit-testable alongside the rest of
/// <see cref="KeyBindingTable"/>.
/// </summary>
public enum InputModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
}

/// <summary>A single (key, modifier) chord. Modifiers match native's
/// <c>ModifierShift/Control/Alt</c> bits (trekinput.cpp:25-28; the port treats
/// <c>ModifierAny</c>=8 as a plain unmodified key because MonoGame reads each
/// chord explicitly).</summary>
public readonly record struct KeyBinding(Keys Key, InputModifiers Modifiers)
{
    /// <summary>Short ASCII-safe label used by the help table and rebind UI,
    /// matching the port's existing presentation (e.g. "C+=", "Ctrl + =",
    /// "PrintScreen"). Never contains a non-ASCII glyph so the ASCII-baked
    /// spritefonts render it.</summary>
    public string Label
    {
        get
        {
            string key = KeyLabel(Key);
            if (Modifiers == InputModifiers.None)
                return key;
            var parts = new List<string>();
            if ((Modifiers & InputModifiers.Control) != 0) parts.Add("Ctrl");
            if ((Modifiers & InputModifiers.Alt) != 0) parts.Add("Alt");
            if ((Modifiers & InputModifiers.Shift) != 0) parts.Add("Shift");
            parts.Add(key);
            return string.Join(" + ", parts);
        }
    }

    /// <summary>Human key name for a MonoGame <see cref="Keys"/> value,
    /// ASCII-only and consistent with the existing help-table wording.</summary>
    public static string KeyLabel(Keys key) => key switch
    {
        Keys.Space => "Space",
        Keys.Enter => "Enter",
        Keys.Escape => "Esc",
        Keys.Back => "Backspace",
        Keys.Tab => "Tab",
        Keys.LeftShift or Keys.RightShift => "Shift",
        Keys.LeftControl or Keys.RightControl => "Ctrl",
        Keys.LeftAlt or Keys.RightAlt => "Alt",
        Keys.Up => "Up", Keys.Down => "Down", Keys.Left => "Left", Keys.Right => "Right",
        Keys.PageUp => "PageUp", Keys.PageDown => "PageDown",
        Keys.Home => "Home", Keys.End => "End", Keys.Insert => "Insert", Keys.Delete => "Delete",
        Keys.D0 => "0", Keys.D1 => "1", Keys.D2 => "2", Keys.D3 => "3", Keys.D4 => "4",
        Keys.D5 => "5", Keys.D6 => "6", Keys.D7 => "7", Keys.D8 => "8", Keys.D9 => "9",
        Keys.PrintScreen => "PrintScreen",
        Keys.OemTilde => "`",
        Keys.OemOpenBrackets => "[",
        Keys.OemCloseBrackets => "]",
        Keys.OemPlus => "=",
        Keys.OemMinus => "-",
        Keys.OemBackslash => "\\",
        Keys.OemPipe => "\\",
        Keys.OemSemicolon => ";",
        Keys.OemQuotes => "'",
        Keys.OemComma => ",",
        Keys.OemPeriod => ".",
        Keys.OemQuestion => "/",
        Keys.None => "",        // P623 — an unbound (rebindable, no default key) command
        _ => key.ToString(),
    };

    /// <summary>True when the physical <paramref name="key"/> plus the current
    /// modifier state satisfies this chord. Mirrors native's "just shift /
    /// just control / just alt" modifier matching (trekinput.cpp:543-577):
    /// a modifier is required only when this binding declares it, and the
    /// declared modifier keys must be down.</summary>
    public bool Matches(KeyboardState ks)
    {
        bool keyDown = ks.IsKeyDown(Key);
        bool wantCtrl = (Modifiers & InputModifiers.Control) != 0;
        bool wantAlt = (Modifiers & InputModifiers.Alt) != 0;
        bool wantShift = (Modifiers & InputModifiers.Shift) != 0;
        bool ctrlDown = ks.IsKeyDown(Keys.LeftControl) || ks.IsKeyDown(Keys.RightControl);
        bool altDown = ks.IsKeyDown(Keys.LeftAlt) || ks.IsKeyDown(Keys.RightAlt);
        bool shiftDown = ks.IsKeyDown(Keys.LeftShift) || ks.IsKeyDown(Keys.RightShift);
        // Required modifiers must be down. Extra un-declared modifiers are
        // tolerated (so Shift+T matches a plain-T chord while typing); this
        // mirrors native binding chords to specific modifier families.
        return keyDown
            && (!wantCtrl || ctrlDown)
            && (!wantAlt || altDown)
            && (!wantShift || shiftDown);
    }
}

/// <summary>
/// P625 - one rebindable command. <see cref="Name"/> is the native TK name from
/// the command-name table (trekinput.cpp:1459) where one exists, or an idm_
/// /port name otherwise; <see cref="Category"/> groups it for the help overlay
/// and the rebind popup exactly as native's <c>g_pCommandInfo</c> categories
/// do (trekinput.cpp:1441-1621). <see cref="Default"/> is the binding the port
/// ships (its effective default); <see cref="NativeDefault"/> is the binding
/// <c>defaultinputmap.mdl</c> assigns, exposed on the rebind popup as
/// "Restore native".
/// </summary>
public sealed record InputActionDef(
    string Name,
    string Category,
    string Description,
    KeyBinding Default,
    KeyBinding? NativeDefault = null,
    IReadOnlyList<KeyBinding>? Alternate = null);

/// <summary>
/// P625 - the live, data-driven action map. Owns the current binding of every
/// action, applies user overrides from a JSON settings file over the
/// <see cref="InputActionCatalog"/>.Definitions defaults, performs conflict
/// detection, exposes reset-to-defaults, and resolves a
/// <see cref="KeyboardState"/> to per-action press/held predicates that every
/// Game1 input handler uses in place of raw <c>XnaKeys</c> literals.
/// </summary>
public sealed class ActionMap
{
    private readonly IReadOnlyDictionary<string, InputActionDef> _defs;
    private readonly Dictionary<string, KeyBinding> _current;

    public ActionMap(IReadOnlyCollection<InputActionDef> defs)
    {
        _defs = defs.ToDictionary(d => d.Name, d => d);
        _current = defs.ToDictionary(d => d.Name, d => d.Default);
    }

    public IReadOnlyCollection<InputActionDef> Definitions => _defs.Values.ToList();

    /// <summary>Current binding of an action (never null; falls back to the
    /// definition default if somehow absent).</summary>
    public KeyBinding BindingOf(string action)
        => _current.TryGetValue(action, out var kb) ? kb : _defs[action].Default;

    /// <summary>P624 — an action's alternate chords (hardware-layout aliases,
    /// e.g. ThrottleFull's ISO/EU backslash scancode OemPipe alongside its
    /// OemBackslash default). Empty when the action has none. Alternates match
    /// in <see cref="IsDown"/> exactly like the primary binding.</summary>
    public IReadOnlyList<KeyBinding> AlternatesOf(string action)
        => _defs.TryGetValue(action, out var def) && def.Alternate is { } alts ? alts : Array.Empty<KeyBinding>();

    /// <summary>An action is "held" right now when its chord (or any alternate
    /// chord) is down.</summary>
    public bool IsDown(KeyboardState ks, string action)
    {
        var kb = BindingOf(action);
        if (kb.Matches(ks)) return true;
        foreach (var alt in AlternatesOf(action))
            if (alt.Matches(ks)) return true;
        return false;
    }

    /// <summary>Raw predicate for callers that need the chord itself.</summary>
    public bool ChordDown(KeyboardState ks, KeyBinding chord) => chord.Matches(ks);

    /// <summary>Assign a new chord to an action, detecting whether it clashes
    /// with another action's current binding. When a clash is found the binding
    /// is NOT applied unless <paramref name="force"/> is true (the rebind UI
    /// offers to swap, mirroring native's InputMapPopup conflict handling).
    /// Returns the name of the conflicting action, or null when free.</summary>
    public string? SetBinding(string action, KeyBinding kb, bool force = false)
    {
        if (!_defs.ContainsKey(action))
            throw new ArgumentException($"unknown action '{action}'", nameof(action));

        string? conflict = null;
        bool clash(KeyBinding a, KeyBinding b) => a.Key == b.Key && a.Modifiers == b.Modifiers;
        foreach (var other in _current)
        {
            if (other.Key == action) continue;
            if (clash(other.Value, kb)) { conflict = other.Key; break; }
            foreach (var alt in AlternatesOf(other.Key))
                if (clash(alt, kb)) { conflict = other.Key; break; }
            if (conflict != null) break;
        }

        if (conflict != null && !force)
            return conflict;

        _current[action] = kb;
        return null;
    }

    /// <summary>Swap this action's binding with another action's (native's
    /// offer when you rebind onto an occupied key).</summary>
    public void SwapBindings(string a, string b)
    {
        var ka = BindingOf(a);
        var kb = BindingOf(b);
        _current[a] = kb;
        _current[b] = ka;
    }

    /// <summary>Restore every action to its catalogue default.</summary>
    public void ResetToDefaults()
    {
        foreach (var d in _defs.Values)
            _current[d.Name] = d.Default;
    }

    /// <summary>Build the help-overlay table groups from the LIVE bindings so a
    /// rebind is reflected immediately (the P625 requirement). Order follows
    /// the catalogue; groups are keyed by category in first-appearance order.</summary>
    public IReadOnlyList<KeyBindingTable.Group> ToHelpGroups()
    {
        var groups = new List<KeyBindingTable.Group>();
        // P623 — commands with NO bound key (Keys.None: the unbound class
        // commands, TK_TargetHostile, the wheel-map MouseWheel convenience) are
        // registered + F6-rebindable but omitted from the help overlay — its
        // EveryCell/NoDuplicateKeys invariants forbid empty/duplicate key cells,
        // and a blank key row is noise.
        foreach (var def in _defs.Values
                     .OrderBy(d => _Order(d.Category))
                     .Where(d => BindingOf(d.Name).Key != Keys.None))
        {
            var title = def.Category;
            var group = groups.FirstOrDefault(g => g.Title == title);
            if (group == default)
            {
                group = new KeyBindingTable.Group(title, new List<KeyBindingTable.Entry>());
                groups.Add(group);
            }
            var entries = group.Entries.ToList();
            entries.Add(new KeyBindingTable.Entry(BindingOf(def.Name).Label, def.Description));
            // Replace the immutable record's entry list by rebuilding the group.
            groups[groups.IndexOf(group)] = group with { Entries = entries };
        }
        return groups;
    }

    private static int _Order(string category)
    {
        int i = Array.IndexOf(InputActionCatalog.CategoryOrder, category);
        return i >= 0 ? i : int.MaxValue;
    }

    /// <summary>Serialize only the user overrides (differences from defaults)
    /// as compact JSON, so a fresh default change on upgrade still flows
    /// through.</summary>
    public string ToJson() =>
        System.Text.Json.JsonSerializer.Serialize(
            _current.Where(kv => kv.Value != _defs[kv.Key].Default)
                    .ToDictionary(kv => kv.Key, kv => new[] { (int)kv.Value.Key, (int)kv.Value.Modifiers }),
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    /// <summary>Apply persisted overrides (format of <see cref="ToJson"/>).
    /// Unknown action names and out-of-range key codes are skipped defensively
    /// so a hand-edited or stale file can't crash the client.</summary>
    public void ApplyJson(string json)
    {
        try
        {
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int[]>>(json);
            if (map == null) return;
            foreach (var (action, kv) in map)
            {
                if (!_defs.ContainsKey(action)) continue;
                if (kv == null || kv.Length < 2) continue;
                var key = (Keys)kv[0];
                var mods = (InputModifiers)kv[1];
                if (!Enum.IsDefined(typeof(Keys), key)) continue;
                // Apply even if it conflicts - the user explicitly chose it; the
                // rebind UI warns live. This matches native persisting whatever
                // the player settled on.
                _current[action] = new KeyBinding(key, mods);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[keymap] ignoring unparsable keymap override JSON: {ex.Message}");
        }
    }
}
