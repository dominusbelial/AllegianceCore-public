using Allegiance.ClientCore.State;
using Allegiance.Core;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-3 — pure resolution of the native bottom target pane. Values follow
/// <c>WinTrek/consoledata.cpp:40-199,202-277</c> (name/type/side/range/speed/
/// hull/shield), <c>WinTrek/WinTrek.cpp:8784-8818</c> (side colour and the
/// neutral grey), and <c>WinTrek/indicator.cpp:405</c> (the lead predictor's
/// in-range flag). Read under <c>ConnectionSession.Gate</c>; no MonoGame or
/// graphics types appear here, mirroring <see cref="SelectionPaneModel"/>.
/// </summary>
public static class TargetPaneModel
{
    // WinTrek/WinTrek.cpp:90 — the brightness multiplier native applies to a
    // sided targetHudColor.
    public const float HudBright = 0.85f;
    // WinTrek/WinTrek.cpp:8784-8818 — SetTargetGeo forces this alpha on both
    // the neutral grey and the sided colour.
    public const float TargetHudAlpha = 0.5f;
    /// <summary>Native's no-side colour: grey (0.5,0.5,0.5) at alpha 0.5
    /// (WinTrek/WinTrek.cpp:8785-8786), not white.</summary>
    public static readonly HudColor Neutral = new(0.5f, 0.5f, 0.5f, TargetHudAlpha);

    public static HudTargetPane Build(
        ClientWorld world,
        IgcObjectType kind,
        short id,
        string? fallbackName = null,
        IReadOnlyDictionary<short, DataHullTypeIGC>? hullTypes = null,
        IReadOnlyDictionary<short, DataStationTypeIGC>? stationTypes = null)
    {
        string? name = Safe(fallbackName);
        string type = "unknown";
        string? sideName = null;
        HudColor sideColor = Neutral;
        float? hull = null, shield = null, ore = null, speed = null;
        bool isShip = false;

        // Native GetRange (consoledata.cpp:182-199) needs the target in the
        // player's cluster; GetPercentHitPoints (202-212) and station shields
        // (272-276) reuse that same test, plus the docked-station exception.
        bool sameCluster = false;
        bool dockedStation = false;
        float? distance = null;
        if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own))
        {
            dockedStation = kind == IgcObjectType.Station && own.StationId >= 0 && own.StationId == id;
            if (own.SectorId >= 0)
            {
                short ownSector = own.SectorId;
                sameCluster = kind switch
                {
                    IgcObjectType.Ship => world.Ships.TryGetValue(id, out var rShip)
                        && !rShip.IsNotVisible && rShip.SectorId == ownSector,
                    IgcObjectType.Station => world.Stations.TryGetValue(id, out var rSt)
                        && rSt.ClusterId == ownSector,
                    IgcObjectType.Asteroid => world.Asteroids.TryGetValue(id, out var rAst)
                        && rAst.ClusterId == ownSector,
                    IgcObjectType.Warp => world.Warps.TryGetValue(id, out var rWarp)
                        && rWarp.ClusterId == ownSector,
                    _ => false,
                };
                if (sameCluster && own.Latest is { } ownUp && TryPosition(world, kind, id, out var targetPos))
                    distance = (targetPos - ownUp.Position).Length();
            }
        }

        switch (kind)
        {
            case IgcObjectType.Ship:
                isShip = true;
                if (world.Ships.TryGetValue(id, out var ship))
                {
                    name = Safe(string.IsNullOrEmpty(ship.CharacterName) ? $"ship {id}" : ship.CharacterName);
                    if (hullTypes is { } hulls && hulls.TryGetValue(ship.HullId, out var hullType))
                        type = Safe(hullType.Name) ?? "unknown";
                    // P373 — only a real EXPORT identity has a trustworthy side.
                    if (ship.HasRealIdentity && ship.SideId is { } sideId)
                    {
                        sideName = SideName(world, sideId);
                        sideColor = SideColor(world, sideId);
                    }
                    if (!ship.IsNotVisible && ship.Latest is { } up)
                    {
                        // Hull is cluster-gated (consoledata.cpp:202-212); ship
                        // shield and speed are not cluster-gated natively
                        // (consoledata.cpp:252-270,136-150 — only CanSee).
                        hull = sameCluster ? up.HullFraction : 0f;
                        shield = up.ShieldFraction;
                        speed = up.Velocity.Length();
                    }
                    else
                    {
                        hull = 0f; shield = 0f; speed = -1f;
                    }
                }
                else
                {
                    hull = 0f; shield = 0f; speed = -1f;
                }
                break;

            case IgcObjectType.Station:
                if (world.Stations.TryGetValue(id, out var st))
                {
                    name = Safe(st.Name);
                    if (stationTypes is { } sts && sts.TryGetValue(st.StationTypeId, out var stt))
                        type = Safe(stt.Name) ?? "unknown";
                    if (st.SideId is { } stSide)
                    {
                        sideName = SideName(world, stSide);
                        sideColor = SideColor(world, stSide);
                    }
                    bool live = world.StationLive.TryGetValue(id, out var state);
                    bool gated = live && (sameCluster || dockedStation);
                    hull = gated ? state.HullFraction : 0f;
                    shield = gated ? state.ShieldFraction : 0f;
                }
                else
                {
                    hull = 0f; shield = 0f;
                }
                speed = 0f;   // static
                break;

            case IgcObjectType.Asteroid:
                if (world.Asteroids.TryGetValue(id, out var ast))
                {
                    name = Safe(ast.Name);
                    type = Safe(NativeAsteroidTypes.GetTypeDefaults(
                        NativeAsteroidTypes.ResolveTypeIndex(ast.AabmCapabilities)).Name) ?? "asteroid";
                    if (world.AsteroidLive.TryGetValue(id, out var live))
                    {
                        hull = sameCluster ? live.Fraction : 0f;   // consoledata.cpp:202-212
                        ore = live.OreFraction;
                    }
                    else
                    {
                        hull = 0f; ore = 0f;
                    }
                }
                else
                {
                    type = "asteroid";
                    hull = 0f;
                }
                speed = 0f;   // static
                break;

            case IgcObjectType.Warp:
                if (world.Warps.TryGetValue(id, out var wp))
                    name = Safe(wp.Name);
                type = "aleph";
                speed = 0f;   // static
                break;

            default:
                speed = 0f;
                break;
        }

        var lead = world.LeadIndicator;
        return new HudTargetPane(
            Present: true,
            Name: name,
            TypeDescription: type,
            SideName: sideName,
            SideColor: sideColor,
            HullFraction: hull,
            ShieldFraction: shield,
            Distance: distance,
            Speed: speed,
            OreFraction: ore,
            KillBonusPercent: null,   // per-target experience is not on the client wire
            InRange: lead.HasLead && lead.InRange,
            IsShip: isShip);
    }

    /// <summary>Printable-ASCII sanitisation (spritefont range 32..126), but
    /// null/empty stays null so renderers keep the native "unknown" fallback.</summary>
    private static string? Safe(string? s) =>
        string.IsNullOrEmpty(s) ? null : ScoreboardLayout.AsciiSafe(s);

    private static string? SideName(ClientWorld world, short sideId)
    {
        var roster = world.SideRoster;
        if (roster is null) return null;
        foreach (var s in roster.Sides)
            if (s.SideId == sideId) return Safe(s.Name);
        return null;
    }

    private static HudColor SideColor(ClientWorld world, short sideId)
    {
        if (world.TryGetSideColor(sideId, out var r, out var g, out var b))
            return new HudColor(r * HudBright, g * HudBright, b * HudBright, TargetHudAlpha);
        return Neutral;
    }

    private static bool TryPosition(ClientWorld world, IgcObjectType kind, short id, out SimVec position)
    {
        switch (kind)
        {
            case IgcObjectType.Ship:
                if (world.Ships.TryGetValue(id, out var ship) && ship.Latest is { } up)
                { position = up.Position; return true; }
                break;
            case IgcObjectType.Station:
                if (world.Stations.TryGetValue(id, out var st))
                { position = new(st.Position[0], st.Position[1], st.Position[2]); return true; }
                break;
            case IgcObjectType.Asteroid:
                if (world.Asteroids.TryGetValue(id, out var ast))
                { position = new(ast.Position[0], ast.Position[1], ast.Position[2]); return true; }
                break;
            case IgcObjectType.Warp:
                if (world.Warps.TryGetValue(id, out var wp))
                { position = new(wp.Position[0], wp.Position[1], wp.Position[2]); return true; }
                break;
        }
        position = default;
        return false;
    }
}
