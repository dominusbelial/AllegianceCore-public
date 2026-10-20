using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.State;

/// <summary>
/// P758 — the static-core lookup surface the part-treasure ack decision needs.
/// Native's client resolves an offered part and its own hull through the local
/// IGC core when answering <c>FM_S_ACQUIRE_TREASURE</c>
/// (<c>clintlib/appmsg.cpp:270-275</c> → <c>IshipIGC::HitTreasure</c>,
/// <c>shipIGC.h:1239-1305</c>). This port's renderer-only client has no core,
/// so the MonoGame head injects its parsed shop catalogue
/// (<c>ShopCatalog.Core</c>/<c>Hulls</c>/<c>Parts</c>) through this interface;
/// <see cref="Session.ConnectionSession"/> uses it to run
/// <see cref="TreasureMountPicker.PickMount"/> against the live world
/// loadout and answer with <c>FM_C_TREASURE_ACK(163)</c> exactly as native
/// does. Without a source (a plain headless session), a part offer is declined
/// with <c>c_mountNA</c> — the server drops the part back into space, so the
/// offer never hangs.
/// </summary>
public interface ITreasureMountSource
{
    /// <summary>The parsed static core (may be null on a degraded catalogue) —
    /// used for part-type equipment lookups and hull capacities.</summary>
    StaticCore? Core { get; }

    /// <summary>Resolve a hull type id, or false when the catalogue has no such
    /// hull.</summary>
    bool TryGetHull(short hullId, out DataHullTypeIGC hull);

    /// <summary>Resolve a part type id, or false when the catalogue has no such
    /// part.</summary>
    bool TryGetPart(short partTypeId, out DataPartTypeIGC part);
}
