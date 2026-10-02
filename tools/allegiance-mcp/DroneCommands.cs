using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P486 — the commander drone tools' pure translation helpers (invest /
/// list_drones / order_drone). Everything here is dependency-free (no wire
/// peers, no live world), so the translation rules are unit-testable without
/// a running server: the command-name → native command-id table (the
/// <c>c_cid*</c> values, <c>src/Igc/igc.h:897-912</c>, mirrored in
/// <see cref="OrderChange"/>), and the target-descriptor → (object type, id)
/// forms the wire's <c>CommandToDrone</c> (P469) resolves.
/// </summary>
internal static class DroneCommands
{
    /// <summary>
    /// Map a commander-facing command name to its native command id
    /// (<see cref="OrderChange.Cid*"/>). Recognized: <c>default</c>,
    /// <c>attack</c>, <c>capture</c>, <c>defend</c>, <c>pickup</c>, <c>goto</c>,
    /// <c>repair</c>, <c>join</c>, <c>mine</c>, <c>build</c>,
    /// <c>donothing</c>/<c>hold</c>. False (cid 0) for anything unrecognized —
    /// the tool refuses rather than guess.
    /// </summary>
    public static bool TryParseCommand(string? name, out short cid)
    {
        cid = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        switch (name.Trim().ToLowerInvariant())
        {
            case "default":  cid = OrderChange.CidDefault;  break;   // 0
            case "attack":   cid = OrderChange.CidAttack;   break;   // 1
            case "capture":  cid = OrderChange.CidCapture;  break;   // 2
            case "defend":   cid = OrderChange.CidDefend;   break;   // 3
            case "pickup":   cid = OrderChange.CidPickup;   break;   // 4
            case "goto":     cid = OrderChange.CidGoto;     break;   // 5
            case "repair":   cid = OrderChange.CidRepair;   break;   // 6
            case "join":     cid = OrderChange.CidJoin;     break;   // 7
            case "mine":     cid = OrderChange.CidMine;     break;   // 8
            case "build":    cid = OrderChange.CidBuild;    break;   // 9
            case "donothing":
            case "hold":     cid = OrderChange.CidDoNothing; break;  // -3
            default:         return false;
        }
        return true;
    }

    /// <summary>
    /// Parse a target descriptor into an (object type, id) pair ready for the
    /// wire, matching the forms the native <c>CommandToDrone</c> target
    /// resolution accepts (P469 <c>ResolveChatOrderTarget</c>). Supported:
    /// <list type="bullet">
    /// <item><c>"asteroid:&lt;id&gt;"</c> / <c>a:</c> / <c>rock:</c> →
    /// <see cref="IgcObjectType.Asteroid"/></item>
    /// <item><c>"ship:&lt;id&gt;"</c> / <c>s:</c> → <see cref="IgcObjectType.Ship"/></item>
    /// <item><c>"station:&lt;id&gt;"</c> / <c>st:</c> → <see cref="IgcObjectType.Station"/></item>
    /// <item><c>"warp:&lt;id&gt;"</c> → <see cref="IgcObjectType.Warp"/></item>
    /// <item>a bare integer → a ship id (the dominant commander use — attack /
    /// goto a ship)</item>
    /// <item><c>""</c> / <c>"none"</c> → <see cref="IgcObjectType"/> invalid,
    /// id NA (targetless, e.g. a DoNothing / voice-only order)</item>
    /// </list>
    /// A <b>name</b> (e.g. an asteroid or ship name) is NOT resolvable here —
    /// it needs a live world lookup — so returns false. Asteroids are addressed
    /// by id only (the wire has no asteroid names, only <c>DataAsteroidIGC</c>
    /// ids).
    /// </summary>
    public static bool TryParseTarget(string? desc, out IgcObjectType ot, out short oid)
    {
        ot = (IgcObjectType)(-1);
        oid = -1;
        if (string.IsNullOrWhiteSpace(desc)) return true;
        desc = desc.Trim();
        if (desc.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        int colon = desc.IndexOf(':');
        if (colon > 0)
        {
            string kind = desc[..colon].Trim().ToLowerInvariant();
            string idPart = desc[(colon + 1)..].Trim();
            if (!short.TryParse(idPart, out short id) || id < 0) return false;
            switch (kind)
            {
                case "asteroid": case "a": case "rock": ot = IgcObjectType.Asteroid; oid = id; return true;
                case "ship": case "s":                  ot = IgcObjectType.Ship;     oid = id; return true;
                case "station": case "st":              ot = IgcObjectType.Station;  oid = id; return true;
                case "warp": case "w":                  ot = IgcObjectType.Warp;     oid = id; return true;
                default: return false;
            }
        }
        if (short.TryParse(desc, out short bare))
        {
            ot = IgcObjectType.Ship; oid = bare; return true;   // bare id → ship
        }
        return false;   // a name needs live lookup — not a form this parser handles
    }

    /// <summary>A short human token for an <see cref="IgcObjectType"/> (result note).</summary>
    public static string KindName(IgcObjectType ot) => ot switch
    {
        IgcObjectType.Ship => "ship",
        IgcObjectType.Asteroid => "asteroid",
        IgcObjectType.Station => "station",
        IgcObjectType.Warp => "warp",
        IgcObjectType.Treasure => "treasure",
        _ => "none",
    };
}
