using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P625b — the source-scanning GUARD that keeps the action-map migration honest.
///
/// The P625 claim "never raw key literals" was false: dozens of
/// <c>IsKeyDown(XnaKeys.…)</c> checks remained hard-coded in the game input
/// files, so rebinding those commands did nothing (and a rebound key whose
/// latch still tracked the old literal could re-fire every frame while held).
/// P625b routed every one of those through the action map (<see
/// cref="Allegiance.ClientCore.MonoGame.Rendering.ActionMap"/>), leaving only
/// an explicit, documented allow-list of raw checks. THIS TEST scans the six
/// game-input files on every run and FAILS if any raw key literal shows up
/// outside that allow-list, so the migration can't silently drift back.
///
/// Raw key use is allowed ONLY for:
///   • TEXT ENTRY / composition — the chat-line Backspace delete and the lobby
///     filter Backspace delete (they edit a text buffer, not a game command);
///   • the rebinding popup's OWN capture — the whole of Game1.KeyBind.cs
///     (it must observe raw keys to know what the player pressed).
/// (The former ThrottleFull OemPipe hardware-layout alias is now folded into
/// the action map as a native alternate — P624 — so it is no longer raw.)
/// Every other raw check fails the test.
/// </summary>
public class P625bActionMapGuardTests
{
    private static readonly string[] ScannedFiles =
    {
        "Game1.FlightInput.cs",
        "Game1.Panes.cs",
        "Game1.Spectator.cs",
        "Game1.Director.cs",
        "Game1.Session.cs",
        "Game1.KeyBind.cs",
        "Game1.Commanding.cs",
        "Game1.FlightParts.cs",
        "Game1.StarmapClick.cs",
        "Game1.TargetCommands.cs",
        "Game1.ConsoleModes.cs",
        "Game1.Targeting.cs",
    };

    // Allow-list entries: (fileName, enclosingMethodName, keyLiteral).
    // "*" for method/key means any. Kept in one place so a legitimate new
    // text-entry/alias raw check is an explicit, reviewed addition.
    private static readonly (string File, string Method, string Key)[] AllowList =
    {
        ("Game1.FlightInput.cs", "PrimeInFlightKeyLatches", "XnaKeys.Back"), // chat-delete backspace prime (text entry)
        ("Game1.FlightInput.cs", "HandleChatCompose", "XnaKeys.Back"),     // chat-delete backspace (text entry; P757 extracted from UpdateFlying; P761's profile editor reuses it)
        ("Game1.Session.cs", "UpdateLobby", "XnaKeys.Back"),                  // lobby filter backspace (text entry)
        ("Game1.Panes.cs", "HandleTeamNameEditKeys", "XnaKeys.Back"),          // P853b team-name edit backspace (text entry; native's edit pane owns VK_BACK, teamscreen.cpp:1012-1029)
        ("Game1.KeyBind.cs", "*", "*"),                                       // rebind popup's own capture
        // P764 — the custom-loadout digit MODIFIERS (load.cpp:2228/2236): native
        // reads them as raw vk flags (ks.bControl/bShift/bAlt) too — they are
        // hardware state, not rebindable game commands.
        ("Game1.Panes.cs", "HandleCustomLoadoutKeys", "Microsoft.Xna.Framework.Input.Keys.LeftControl"),
        ("Game1.Panes.cs", "HandleCustomLoadoutKeys", "Microsoft.Xna.Framework.Input.Keys.RightControl"),
        ("Game1.Panes.cs", "HandleCustomLoadoutKeys", "Microsoft.Xna.Framework.Input.Keys.LeftShift"),
        ("Game1.Panes.cs", "HandleCustomLoadoutKeys", "Microsoft.Xna.Framework.Input.Keys.RightShift"),
        ("Game1.Panes.cs", "HandleCustomLoadoutKeys", "Microsoft.Xna.Framework.Input.Keys.LeftAlt"),
        ("Game1.Panes.cs", "HandleCustomLoadoutKeys", "Microsoft.Xna.Framework.Input.Keys.RightAlt"),
    };

    private static readonly Regex RawLiteral = new(
        @"IsKeyDown\(\s*(?<key>XnaKeys\.[A-Za-z0-9_]+|Keys\.[A-Za-z0-9_]+|Microsoft\.Xna\.Framework\.Input\.Keys\.[A-Za-z0-9_]+)",
        RegexOptions.Compiled);

    [Fact]
    public void NoRawKeyLiteralsOutsideAllowList_InGameInputFiles()
    {
        string root = RepoRoot();
        var violations = new List<string>();
        var seen = new HashSet<string>();

        foreach (var file in ScannedFiles)
        {
            string path = Path.Combine(root, "src", "Allegiance.ClientCore.Game", file);
            Assert.True(File.Exists(path), $"expected source file {path}");
            string[] lines = File.ReadAllLines(path);
            string[] methodAt = EnclosingMethods(lines);

            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match m in RawLiteral.Matches(lines[i]))
                {
                    string key = m.Groups["key"].Value;
                    string method = methodAt[i];
                    seen.Add($"{file}:{key}");
                    if (!IsAllowed(file, method, key))
                        violations.Add($"  {file}:{i + 1} [{method}] uses raw {key} — route through ActionDown/KeyOf and remove it, or justify it in the P625b allow-list.");
                }
            }
        }

        // Sanity: every allow-listed entry is actually present (nothing stale).
        foreach (var (file, method, key) in AllowList)
        {
            if (key == "*" || method == "*") continue;
            Assert.True(seen.Contains($"{file}:{key}"),
                $"allow-list entry ({file}, {method}, {key}) is stale — that raw check no longer exists; remove it.");
        }

        Assert.True(violations.Count == 0,
            "Raw key literals must not appear in the game input files outside the documented allow-list:\n"
            + string.Join("\n", violations));
    }

    private static bool IsAllowed(string file, string method, string key)
        => AllowList.Any(a =>
            a.File == file
            && (a.Method == "*" || a.Method == method)
            && (a.Key == "*" || a.Key == key));

    /// <summary>Walk up from the test bin dir to the AllegianceCore repo root.</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "Allegiance.ClientCore.MonoGame")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new InvalidOperationException("cannot locate AllegianceCore repo root");
        return dir;
    }

    /// <summary>
    /// Lightweight enclosing-method scanner: for each source line returns the
    /// name of the method whose body it sits in ("" = class level). Handles
    /// multi-line signatures and expression-bodied members. Robust enough for
    /// the guard's purpose — it only needs to name the handful of allowed raw
    /// checks; a mis-attribution fails loudly, never silently passes.
    /// </summary>
    private static string[] EnclosingMethods(string[] lines)
    {
        var sig = new Regex(@"\b(private|public|internal|protected|static)\b[^{;]*\b(?<name>[A-Za-z_][A-Za-z0-9_<>\[\]?,.]*)\s*\(");
        int n = lines.Length;
        string[] outNames = new string[n];
        string cur = "";
        int depth = 0;
        string? pending = null;
        bool exprBody = false;

        for (int i = 0; i < n; i++)
        {
            string body = lines[i].Split("//")[0];
            string active = cur;
            var m = sig.Match(body);
            if (m.Success && depth == 1)
            {
                pending = m.Groups["name"].Value;
                exprBody = false;
            }
            if (pending != null && body.Contains("=>"))
            {
                active = pending;
                exprBody = true;
                pending = null;
            }
            outNames[i] = active;

            int opens = Count(body, '{');
            int closes = Count(body, '}');
            if (pending != null && opens > closes && depth == 1)
            {
                cur = pending;
                pending = null;
                depth += opens - closes;
                continue;
            }

            int nd = depth + opens - closes;
            if (cur != "")
            {
                if (exprBody)
                {
                    if (body.Contains(';')) { cur = ""; exprBody = false; }
                }
                else if (nd < 2)
                {
                    cur = "";
                }
            }
            depth = nd;
        }
        return outNames;
    }

    private static int Count(string s, char c)
    {
        int cnt = 0;
        foreach (var ch in s) if (ch == c) cnt++;
        return cnt;
    }
}
