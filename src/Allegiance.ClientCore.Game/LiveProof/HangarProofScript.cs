using System.Globalization;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.LiveProof;

/// <summary>One scripted hangar-screen action, timed in seconds after the
/// hangar screen first appeared (the ship docked).</summary>
public readonly record struct HangarScriptStep(HangarScriptAction Action, HangarAction Verb, double AtSeconds);

public enum HangarScriptAction
{
    /// <summary>Park the pointer away from every pane (hover-none).</summary>
    Idle,
    /// <summary>Put the pointer on a verb's pane (native hover face + hover text).</summary>
    Hover,
    /// <summary>Click a verb through the real hangar hit test / dispatcher.</summary>
    Click,
    /// <summary>Leave the loadout screen for the hangar (the Escape path).</summary>
    Back,
    /// <summary>Take a real-backbuffer screenshot now.</summary>
    Shot,
}

/// <summary>
/// P945 — the scripted hangar driver behind <c>--auto-hangar</c>, so a real-window
/// proof can photograph the native hangar screen idle, hovered and clicked
/// without an operator moving the real pointer:
///
/// <code>--auto-hangar "hover:launch@1,shot@1.5,hover:loadout@2,shot@2.5,\
/// click:loadout@3,shot@3.5,back@4,shot@4.5,click:launch@5,shot@8"</code>
///
/// <para>The steps only feed the production paths: <see cref="Hover"/> parks the
/// pointer on the rectangle <see cref="HangarScreen"/> says is drawn,
/// <see cref="Click"/> runs the same <c>Hit</c> → <c>ActivateHangar</c> dispatch a
/// real click does, and <see cref="Shot"/> is the ordinary P572 autoshot. Pure
/// parsing (no game state) so a typo cannot silently re-time a run.</para>
/// </summary>
public static class HangarProofScript
{
    private static readonly Dictionary<string, HangarAction> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["launch"] = HangarAction.Launch,
        ["buy"] = HangarAction.Buy,
        ["command"] = HangarAction.Command,
        ["loadout"] = HangarAction.Loadout,
        ["default"] = HangarAction.Default,
        ["invest"] = HangarAction.Invest,
        ["help"] = HangarAction.Help,
        ["teleport"] = HangarAction.Teleport,
        ["team"] = HangarAction.Team,
        ["exit"] = HangarAction.Exit,
    };

    /// <summary>Parse <c>step[:verb]@seconds,...</c> into time-ordered steps.
    /// Malformed entries are reported on stderr and skipped.</summary>
    public static HangarScriptStep[] Parse(string script)
    {
        var steps = new List<HangarScriptStep>();
        foreach (var raw in script.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string entry = raw.Trim();
            int at = entry.LastIndexOf('@');
            if (at <= 0 || at == entry.Length - 1)
            {
                Console.Error.WriteLine($"[autoproof] --auto-hangar: expected step[:verb]@seconds, got '{entry}' — skipped");
                continue;
            }
            string head = entry[..at].Trim();
            if (!double.TryParse(entry[(at + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var when)
                || when < 0)
            {
                Console.Error.WriteLine($"[autoproof] --auto-hangar: bad time in '{entry}' — skipped");
                continue;
            }
            int colon = head.IndexOf(':');
            string kind = colon < 0 ? head : head[..colon];
            string verb = colon < 0 ? "" : head[(colon + 1)..];
            switch (kind.ToLowerInvariant())
            {
                case "idle" or "none":
                    steps.Add(new HangarScriptStep(HangarScriptAction.Idle, HangarAction.None, when));
                    break;
                case "shot" or "screenshot":
                    steps.Add(new HangarScriptStep(HangarScriptAction.Shot, HangarAction.None, when));
                    break;
                case "back":
                    steps.Add(new HangarScriptStep(HangarScriptAction.Back, HangarAction.None, when));
                    break;
                case "hover":
                case "click":
                    if (!Verbs.TryGetValue(verb, out var action) || action == HangarAction.None)
                    {
                        Console.Error.WriteLine($"[autoproof] --auto-hangar: unknown verb '{verb}' in '{entry}' — skipped");
                        break;
                    }
                    steps.Add(new HangarScriptStep(
                        kind.Equals("hover", StringComparison.OrdinalIgnoreCase)
                            ? HangarScriptAction.Hover : HangarScriptAction.Click, action, when));
                    break;
                default:
                    Console.Error.WriteLine($"[autoproof] --auto-hangar: unknown step '{kind}' in '{entry}' — skipped");
                    break;
            }
        }
        steps.Sort((a, b) => a.AtSeconds.CompareTo(b.AtSeconds));
        return steps.ToArray();
    }
}
