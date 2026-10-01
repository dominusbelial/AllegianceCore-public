using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.QuickChat;

/// <summary>A quick-chat command's resolved order on the wire: the <c>CommandID</c>
/// to send plus the resolved target's object type + id. When
/// <see cref="CarriesOrder"/> is false the chat is voice-only (cid = CidNone),
/// exactly as native's <c>SendQuickChat</c> falls back to when it cannot find a
/// target (<c>console.cpp:1586-1588</c>).</summary>
public readonly record struct ResolvedQuickChatOrder(short CommandId, IgcObjectType OtTarget, short OidTarget)
{
    /// <summary>True iff this is a real order (a command id, not c_cidNone).</summary>
    public bool CarriesOrder => CommandId != OrderChange.CidNone;
}

/// <summary>
/// P457 — the port of native <c>SendQuickChat</c>'s target-resolution half
/// (<c>WinTrek/console.cpp:1534-1600</c>): given a quick-chat command's resolved
/// <c>cid</c> and <c>targetType</c> mask, resolve the target the command names,
/// falling back to <c>cid = c_cidNone</c> (voice-only) when no target is found.
/// Pure and headless so the mask semantics are unit-testable without a graphics
/// device.
/// </summary>
/// <remarks>
/// <para><b>Faithful to native's switch</b> (<c>console.cpp:1549-1584</c>): the
/// special single-bit masks (<c>ttMyAccepted</c>/<c>ttMyQueued</c>/<c>ttMyTarget</c>,
/// <c>ttWorstEnemy</c>, <c>ttMe</c>) are handled directly; everything else falls
/// to the <i>current target if it matches, else the nearest matching object in the
/// player's own sector</i> family rule. <c>ttAnyCluster</c> — which in native
/// widens <c>FindTarget</c> beyond the current sector — is documented but NOT
/// honoured here: the port's client gathers same-sector candidates only
/// (<c>GatherSectorCandidates</c>), so a family mask containing
/// <c>ttAnyCluster</c> still searches the player's own sector only (the four real
/// commands that carry it — <c>gotoBaseCommand</c>/<c>headBackCommand</c> — target
/// friendly stations, which the player's sector candidate pool already covers in
/// normal play).</para>
/// <para><b>Documented gaps (no matching client state):</b>
/// <c>cidMyAccepted</c> resolves to the sender's own <b>accepted</b> command id
/// in native (<c>console.cpp:1538-1539</c>); the port's renderer client tracks only
/// the <b>queued</b> order (<see cref="QueuedCommandState"/>, P424), no accepted
/// slot, so it degrades to none. Likewise <c>ttMyAccepted</c> reads the port's
/// single queued-order state (P424) as the closest available analogue; a truly
/// separate accepted-command state does not exist client-side. <c>ttWorstEnemy</c>
/// needs the sender's damage track (<c>console.cpp:1561-1567</c>), which the port
/// has no analogue of — it resolves to none. Each gap degrades the command to
/// voice-only rather than inventing a target.</para>
/// </remarks>
internal static class QuickChatOrderResolver
{
    // The special c_ttMy* / c_ttMe target masks (quickchat.mdl top, matching igc.h).
    public const long TtMyAccepted = 0x80000;
    public const long TtMyQueued   = 0x80001;
    public const long TtMyTarget   = 0x80002;
    public const long TtWorstEnemy = 0x80003;
    public const long TtMe         = 0x80004;

    // Family flag bits (quickchat.mdl top).
    public const long TtFriendly  = 0x0001;
    public const long TtEnemy     = 0x0002;
    public const long TtNeutral   = 0x0004;
    public const long TtShip      = 0x0008;
    public const long TtStation   = 0x0010;
    public const long TtAsteroid  = 0x0020;
    public const long TtTreasure  = 0x0040;
    public const long TtWarp      = 0x0100;
    public const long TtNearest   = 0x0800;
    public const long TtAnyCluster = 0x1000;

    private const long SideMaskAll = TtFriendly | TtEnemy | TtNeutral;

    /// <summary>Native <c>c_cidMyAccepted = -2</c> (<c>igc.h:899</c>).</summary>
    public const long CidMyAccepted = -2;

    /// <summary>
    /// Resolve a quick-chat command (its parsed <paramref name="commandId"/> and
    /// <paramref name="ttMask"/>/<paramref name="abilityMask"/>) to the wire order.
    /// </summary>
    /// <param name="commandId">The command's resolved cid (a real order id, or
    /// <see cref="OrderChange.CidNone"/> for a voice-only leaf).</param>
    /// <param name="ttMask">The command's resolved target-type mask.</param>
    /// <param name="abilityMask">The command's resolved ability mask (0 = any).</param>
    /// <param name="selfId">The local ship's id (the <c>ttMe</c> target).</param>
    /// <param name="mySideId">The local ship's side (for friendly/enemy filters).</param>
    /// <param name="selfPos">The local ship's position (nearest-family origin).</param>
    /// <param name="selectedTarget">The T-selected / current autopilot target.</param>
    /// <param name="myQueuedCommand">The local player's P424 queued-order state.</param>
    /// <param name="sectorCandidates">The player's same-sector candidate objects
    /// (<see cref="Targeting.GatherSectorCandidates"/>).</param>
    public static ResolvedQuickChatOrder Resolve(
        long commandId, long ttMask, long abilityMask,
        short selfId, short? mySideId, SimVec selfPos,
        Game1.AutopilotTarget? selectedTarget,
        QueuedCommandState? myQueuedCommand,
        IReadOnlyList<Game1.CandidateObject> sectorCandidates)
    {
        short cid = (short)commandId;
        if (cid == (short)CidMyAccepted)
            cid = OrderChange.CidNone;      // no accepted-command state client-side (documented gap)

        (IgcObjectType Ot, short Oid)? target = null;
        if (cid != OrderChange.CidNone)
        {
            target = ResolveTarget(ttMask, abilityMask, selfId, mySideId, selfPos,
                                   selectedTarget, myQueuedCommand, sectorCandidates);
            if (target is null) cid = OrderChange.CidNone;
        }
        return new ResolvedQuickChatOrder(cid, target?.Ot ?? (IgcObjectType)(-1), target?.Oid ?? -1);
    }

    private static (IgcObjectType, short)? ResolveTarget(
        long ttMask, long abilityMask, short selfId, short? mySideId, SimVec selfPos,
        Game1.AutopilotTarget? selectedTarget, QueuedCommandState? myQueuedCommand,
        IReadOnlyList<Game1.CandidateObject> sectorCandidates)
    {
        switch (ttMask)
        {
            case TtMyAccepted:
            case TtMyQueued:
                return FromQueuedCommand(myQueuedCommand);
            case TtMyTarget:
                return selectedTarget is { } st ? FromSelected(st) : null;
            case TtWorstEnemy:
                return null;                 // no damage track (documented gap)
            case TtMe:
                return (IgcObjectType.Ship, selfId);
            default:
                return ResolveFamily(ttMask, abilityMask, selfId, mySideId, selfPos,
                                     selectedTarget, sectorCandidates);
        }
    }

    private static (IgcObjectType, short)? FromQueuedCommand(QueuedCommandState? q)
    {
        if (q is not { } queued) return null;
        if (queued.CommandId == OrderChange.CidNone || queued.TargetId < 0) return null;
        return (queued.TargetType, queued.TargetId);
    }

    private static (IgcObjectType, short)? FromSelected(in Game1.AutopilotTarget t)
        => (KindToWire(t.Kind), t.Id);

    /// <summary>Nearest-family resolution (native's <c>default</c> case,
    /// console.cpp:1574-1583): the current/selected target when it is findable
    /// under the mask, else the nearest same-sector candidate that matches.</summary>
    private static (IgcObjectType, short)? ResolveFamily(
        long ttMask, long abilityMask, short selfId, short? mySideId, SimVec selfPos,
        Game1.AutopilotTarget? selectedTarget, IReadOnlyList<Game1.CandidateObject> sectorCandidates)
    {
        if (selectedTarget is { } st)
        {
            var sel = sectorCandidates.FirstOrDefault(c => c.Kind == st.Kind && c.Id == st.Id);
            if (sel.Id == st.Id && MatchesMask(in sel, ttMask, abilityMask, mySideId, selfId))
                return FromSelected(st);
        }

        Game1.CandidateObject best = default;
        bool found = false;
        float bestD = float.MaxValue;
        foreach (var c in sectorCandidates)
        {
            if (!MatchesMask(in c, ttMask, abilityMask, mySideId, selfId)) continue;
            var d = (c.Position - selfPos).LengthSquared();
            if (d < bestD) { bestD = d; best = c; found = true; }
        }
        if (!found) return null;
        return (KindToWire(best.Kind), best.Id);
    }

    /// <summary>The findable-model gate (native <c>FindableModel</c>,
    /// common.cpp:254-353, simplified to what the port's client can classify):
    /// the candidate must not be self, match the mask's type bit, match the
    /// mask's side relation, and (when an ability mask is given) carry it.</summary>
    private static bool MatchesMask(in Game1.CandidateObject c, long ttMask, long abilityMask,
        short? mySideId, short selfId)
    {
        if (c.Id == selfId) return false;

        long typeBit = c.Kind switch
        {
            Game1.TargetKind.Ship => TtShip,
            Game1.TargetKind.Station => TtStation,
            Game1.TargetKind.Asteroid => TtAsteroid,
            Game1.TargetKind.Warp => TtWarp,
            _ => 0,
        };
        if ((ttMask & typeBit) == 0) return false;

        long sideMask = ttMask & SideMaskAll;
        bool sideOk;
        if (sideMask == 0) sideOk = true;                                    // any side
        else if ((sideMask & TtFriendly) != 0 && c.SideId is { } fs && fs == mySideId) sideOk = true;
        else if ((sideMask & TtEnemy) != 0 && c.SideId is { } es && es != mySideId) sideOk = true;
        else if ((sideMask & TtNeutral) != 0 && c.SideId is null) sideOk = true;
        else sideOk = false;
        if (!sideOk) return false;

        if (abilityMask != 0 && (c.RoleBits & abilityMask) == 0) return false;
        return true;
    }

    private static IgcObjectType KindToWire(Game1.TargetKind k) => k switch
    {
        Game1.TargetKind.Ship => IgcObjectType.Ship,
        Game1.TargetKind.Station => IgcObjectType.Station,
        Game1.TargetKind.Asteroid => IgcObjectType.Asteroid,
        Game1.TargetKind.Warp => IgcObjectType.Warp,
        _ => (IgcObjectType)(-1),
    };
}
