using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P857 — NPC warp parity: native NPCs (drones: miners, constructors,
/// carriers, fighters) never leave <c>warpReady</c> and have neither the
/// player update handshake, a 2&nbsp;s re-warp cooldown, nor an arrival
/// latch (<c>FedSrv/fsship.cpp:74-82,1185-1191</c>). Native delegates the
/// warp collision to the site (<c>Igc/shipIGC.cpp:620-636</c>); a native NPC
/// that re-enters a cone warps at once — it normally clears the arrival aleph
/// via the cruise-speed transit + exit push (the P591c routing fix) so
/// re-entry is the exception, exactly as for a ready player.
///
/// The port kept two NPC-only rules in <see cref="Sector.ResolveAlephShip"/>
/// (recorded as the remaining SIM-0150/0208 gaps in P788C): the 2&nbsp;s
/// <see cref="Sector.WarpCooldownSeconds"/> window and the opt-in 2×
/// arrival-volume latch (<see cref="Ship.LastArrivalWarpId"/>). P857 drops
/// both for <c>IsNpc</c> ships so they warp exactly as native. Players keep
/// the cooldown window (mirroring native's <c>m_warpState</c> re-arm) and, when
/// the opt-in non-native re-entry guard is enabled, the arrival latch.
///
/// Each test here fails on the pre-P857 code (NPCs gated by the cooldown /
/// latch) and passes after.
/// </summary>
// Toggles the process-global Sector.WarpReEntryGuardEnabled, as P591cDroneRoutingTests does:
// same non-parallel collection so neither flips it under the other.
[Collection("LiveNetwork")]
public class P857NpcWarpParityTests
{
    // Same corpus-derived core + warp-bearing map as WarpProximityTests (P776),
    // so the cone trigger is exercised against the real native hit-test shape.
    private static readonly Lazy<StaticCore> Core = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new System.InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> MapWithWarp = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Warps.Count == 0) continue;
            return map;
        }
        throw new System.InvalidOperationException("no warp-bearing map found in corpus");
    });

    private static Sector NewStartedSector(out Mission mission, out RecordingIgcSite site)
    {
        var civ = Core.Value.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        mission = Mission.Assemble(
            Core.Value, MapWithWarp.Value,
            [new SideSpec(0, "Team 1", civ.CivilizationId)], site = new RecordingIgcSite());
        mission.Stage = MissionStage.Started;
        return mission.Sectors.First(s => s.Warps.Any(w => w.Destination is not null && w.Warp.Position is { Length: 3 }));
    }

    private static ResolvedWarp EntryWarp(Sector sector) =>
        sector.Warps.First(w => w.Destination is not null && w.Warp.Position is { Length: 3 });

    private static Vector3 WarpPosition(ResolvedWarp warp) =>
        new(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);

    private static Vector3 WarpForward(ResolvedWarp warp) =>
        warp.Warp.Forward is { Length: 3 } f ? new Vector3(f[0], f[1], f[2]) : new Vector3(0f, 0f, -1f);

    private static Vector3 WarpBackward(ResolvedWarp warp) => AlephConeHitTest.Backward(WarpForward(warp));

    private static int HitWarpEvents(RecordingIgcSite site) =>
        site.Calls.Count(c => c.StartsWith("HitWarpEvent"));

    private static void TickN(Mission m, int n)
    {
        double t = m.LastUpdate;
        for (int i = 0; i < n; i++) { t += 0.045; m.Update(t); }   // < 0.25 ⇒ one chunk
    }

    private static Ship NewShip(short id, DataHullTypeIGC hull, Vector3 position, bool isNpc) =>
        new(id, hull, position, Vector3.Zero, Orientation.Identity) { IsNpc = isNpc };

    /// <summary>⭐ An NPC that JUST warped (LastWarpTime set this sim-instant)
    /// and is still inside the cone re-triggers IMMEDIATELY — native NPCs have
    /// no re-warp cooldown (<c>fsship.cpp:74-82</c>). On today's code the
    /// 2&nbsp;s <see cref="Sector.WarpCooldownSeconds"/> window silences the
    /// NPC for the whole run, so HitWarpEvent never fires: this fails first.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Npc_recently_warped_and_still_inside_cone_rewarps_immediately()
    {
        Require.Artwork();
        var sector = NewStartedSector(out var mission, out var site);
        var warp = EntryWarp(sector);
        var warpPos = WarpPosition(warp);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();

        var npc = NewShip(9101, hull, warpPos + WarpBackward(warp) * Math.Max(1f, 0.3f * warp.Warp.Radius), isNpc: true);
        mission.PlaceShip(npc, sector);
        // The host sets LastWarpTime on transit; represent "just warped this instant".
        npc.LastWarpTime = mission.LastUpdate;

        TickN(mission, 5);   // 0.225 s — comfortably inside the 2 s window

        Assert.True(HitWarpEvents(site) >= 1,
            "an NPC that just warped must re-warp immediately on re-entering the cone (native: no NPC cooldown, fsship.cpp:74-82)");
    }

    /// <summary>⭐ Counterpart guard: a PLAYER that just warped is still
    /// debounced by the 2&nbsp;s window (native <c>m_warpState</c> re-arm,
    /// fsship.cpp:1185-1191). P857 keeps the player window, so this passes on
    /// both today's code and the change — it pins that the cooldown removal is
    /// NPC-only.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Player_recently_warped_and_still_inside_cone_is_debounced()
    {
        Require.Artwork();
        var sector = NewStartedSector(out var mission, out var site);
        var warp = EntryWarp(sector);
        var warpPos = WarpPosition(warp);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();

        var player = NewShip(9201, hull, warpPos + WarpBackward(warp) * Math.Max(1f, 0.3f * warp.Warp.Radius), isNpc: false);
        mission.PlaceShip(player, sector);
        player.LastWarpTime = mission.LastUpdate;

        TickN(mission, 5);   // 0.225 s < 2 s cooldown

        Assert.Equal(0, HitWarpEvents(site));
    }

    /// <summary>⭐ Even with the opt-in non-native re-entry guard ENABLED, an NPC
    /// is NOT held by the arrival latch — native NPCs are not latched
    /// (fsship.cpp:74-82). The NPC has just arrived through this warp
    /// (<see cref="Ship.LastArrivalWarpId"/> set) but its route goal is a
    /// different warp; P857 makes the guard player-only, so the NPC re-crosses
    /// at once. On today's code the guard blocks it: fails first.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Npc_is_not_held_by_arrival_latch_even_with_guard_enabled()
    {
        Require.Artwork();
        bool prior = Sector.WarpReEntryGuardEnabled;
        Sector.WarpReEntryGuardEnabled = true;   // the opt-in non-native safety net
        try
        {
        var sector = NewStartedSector(out var mission, out var site);
        var warp = EntryWarp(sector);
        var warpPos = WarpPosition(warp);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();

        var npc = NewShip(9301, hull, warpPos + WarpBackward(warp) * Math.Max(1f, 0.3f * warp.Warp.Radius), isNpc: true);
        mission.PlaceShip(npc, sector);
        npc.LastArrivalWarpId = warp.Warp.WarpId;              // just arrived through it
        npc.IntendedNextWarpId = (short)(warp.Warp.WarpId + 1); // route goal is elsewhere

        TickN(mission, 5);

        Assert.True(HitWarpEvents(site) >= 1,
            "an NPC must not be held by the arrival latch (native NPCs are not latched, fsship.cpp:74-82)");
        }
        finally { Sector.WarpReEntryGuardEnabled = prior; }
    }

    /// <summary>⭐ Counterpart guard: with the opt-in re-entry guard ENABLED, a
    /// PLAYER that just arrived through the warp and is still inside its volume
    /// IS held from unintentionally re-crossing (unless its route goal is that
    /// very warp). P857 keeps the guard for players, so this passes both
    /// before and after — it pins that the latch removal is NPC-only.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Player_is_held_by_arrival_latch_when_guard_enabled()
    {
        Require.Artwork();
        bool prior = Sector.WarpReEntryGuardEnabled;
        Sector.WarpReEntryGuardEnabled = true;
        try
        {
        var sector = NewStartedSector(out var mission, out var site);
        var warp = EntryWarp(sector);
        var warpPos = WarpPosition(warp);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();

        var player = NewShip(9401, hull, warpPos + WarpBackward(warp) * Math.Max(1f, 0.3f * warp.Warp.Radius), isNpc: false);
        mission.PlaceShip(player, sector);
        player.LastArrivalWarpId = warp.Warp.WarpId;              // just arrived through it
        player.IntendedNextWarpId = (short)(warp.Warp.WarpId + 1); // route goal is elsewhere

        TickN(mission, 5);

        Assert.Equal(0, HitWarpEvents(site));
        }
        finally { Sector.WarpReEntryGuardEnabled = prior; }
    }
}
