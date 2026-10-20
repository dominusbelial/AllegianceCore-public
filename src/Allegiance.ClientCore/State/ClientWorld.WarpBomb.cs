using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    /// <summary>P758 — one live warp bomb, the client-side port of native
    /// <c>IwarpIGC::AddBomb</c> as driven by the <c>FM_S_WARP_BOMB(188)</c>
    /// handler (<c>clintlib/appmsg.cpp:850-868</c>): the bombed aleph remembers
    /// its detonation deadline (converted from the server clock) and the
    /// expendable type that hit it, until the blast resolves.</summary>
    public readonly record struct WarpBombState(
        double ExplosionServerSeconds,
        short MissileTypeId,
        double ReceivedSeconds);

    private readonly Dictionary<short, WarpBombState> _warpBombs = new();

    /// <summary>P758 — live warp bombs keyed by the bombed warp's id
    /// (<see cref="DataWarpIGC.WarpId"/>). Empty until a <c>WARP_BOMB(188)</c>
    /// arrives; cleared with the rest of the world on a fresh connect.</summary>
    public IReadOnlyDictionary<short, WarpBombState> WarpBombs => _warpBombs;

    /// <summary>P758 — the running estimate of the server's simulation clock in
    /// seconds (seeded by PING/treasure-clock samples, extrapolated with the
    /// live <see cref="TimeScale"/>). Native converts the wire explosion time
    /// with <c>ClientTimeFromServerTime</c> (<c>appmsg.cpp:861</c>); the port
    /// compares <see cref="WarpBombState.ExplosionServerSeconds"/> against this
    /// clock to drive the destabilized-aleph countdown.</summary>
    public double ServerClockSeconds => _treasureClockSeeded ? TreasureServerSeconds() : 0;

    /// <summary>P758 — <c>FM_S_WARP_BOMB(188)</c>, the port of native's client
    /// handler (<c>clintlib/appmsg.cpp:850-868</c>):
    /// <list type="bullet">
    /// <item>gated on <c>IsInGame()</c> (:852) — a lobby client ignores it;</item>
    /// <item>the bombed warp is resolved from the world table and remembers the
    /// bomb (<c>pwarp-&gt;AddBomb(ClientTimeFromServerTime(timeExplosion), pmt)</c>,
    /// :858-866) — an unknown warp id is ignored (native's <c>if (pwarp)</c>);</item>
    /// <item>when the LOCAL ship's sector is either end of the bombed aleph, the
    /// critical console line <c>"Aleph to %s destabilized"</c> names the aleph's
    /// OTHER sector (:867-868).</item>
    /// </list>
    /// The message is whole-mission news (the server broadcasts it to every
    /// side, <c>FedSrv.CPP:12558</c>), so the state is recorded for every
    /// bombed aleph — only the console line is sector-gated.</summary>
    private void Apply(WarpBomb m)
    {
        if (!InGame) return;                                   // IsInGame(), appmsg.cpp:852
        if (!_warps.TryGetValue(m.WarpIdBombed, out var warp)) return;   // if (pwarp), :857

        _warpBombs[m.WarpIdBombed] = new WarpBombState(
            ExplosionServerSeconds: m.TimeExplosion / 1000.0,  // ClientTimeFromServerTime, :861
            MissileTypeId: m.ExpendableIdMissile,
            ReceivedSeconds: NowSeconds());

        // appmsg.cpp:867-868: only a pilot whose current cluster is one of the
        // aleph's two ends is told — and the line names the OTHER end.
        if (AlertOtherSector(warp, out short other))
            PostSystemLine($"Aleph to {ClusterName(other)} destabilized");
    }

    /// <summary>Pure half of the native alert decision
    /// (<c>appmsg.cpp:863-868</c>): returns the sector on the FAR side of the
    /// bombed aleph when the local ship's current sector is one of its two
    /// ends, else <c>false</c>.</summary>
    private bool AlertOtherSector(DataWarpIGC warp, out short otherSector)
    {
        otherSector = -1;
        if (MyShipId is not { } me || !_ships.TryGetValue(me, out var own)) return false;
        short here = own.PoseSectorId ?? -1;
        if (here < 0) return false;
        if (warp.ClusterId == here)
        {
            // We sit in the bombed aleph's own sector — name the destination's
            // sector (the partner warp's cluster).
            return _warps.TryGetValue(warp.DestinationId, out var partner)
                && (otherSector = partner.ClusterId) >= 0;
        }
        if (_warps.TryGetValue(warp.DestinationId, out var dest) && dest.ClusterId == here)
        {
            // We sit at the far end — name the bombed aleph's own sector.
            otherSector = warp.ClusterId;
            return otherSector >= 0;
        }
        return false;
    }

    private string ClusterName(short clusterId)
        => _clusters.TryGetValue(clusterId, out var cluster) ? cluster.Name : $"sector {clusterId}";
}
