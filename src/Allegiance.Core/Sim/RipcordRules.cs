using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>
/// P754b — the real rule behind <see cref="IIgcSite.ContinueRipcord"/>: the port
/// of <c>FedSrvSiteBase::ContinueRipcord</c> (<c>src/FedSrv/FedSrv.CPP:13379-13437</c>).
/// </summary>
/// <remarks>
/// <para><b>Why this is not in the sector any more.</b> P754 left the rule in
/// <c>Sector.ResolveRipcordCountdowns</c> (a private <c>RipcordAnchorStillLegal</c>)
/// and gave the site a callback that only returned a constant. Native decides the
/// whole thing inside the site method and the sector merely consumes the boolean
/// (<c>shipIGC.cpp:277-292</c>: <c>if (pigc-&gt;ContinueRipcord(this, m_pmodelRipcord))</c>
/// continues the countdown, else <c>SetRipcordModel(NULL)</c>). The rule therefore
/// lives here, in one place, and both the <see cref="IIgcSite"/> default body and
/// the production host (<c>Allegiance.Server.ServerHost.ContinueRipcord</c>) call
/// it — the sector has no copy.</para>
/// <para><b>The native legs</b> (identical in the <c>bAllowAlliedRip</c> and the
/// regular branch apart from the allied side test):</para>
/// <list type="number">
/// <item><c>pship-&gt;GetFlag() != NA</c> — a flag carrier can never ripcord
/// (<c>FedSrv.CPP:13385,13405</c>).</item>
/// <item><c>pship-&gt;GetSide() != pmodel-&gt;GetSide()</c> (plus the
/// <c>AlliedSides</c> leg when <c>misparms.bAllowAlliedRip</c>) — the anchor must
/// be friendly (<c>FedSrv.CPP:13386,13406</c>).</item>
/// <item><c>pmodel-&gt;GetCluster() == NULL</c> — an anchor that left its cluster
/// (docked / destroyed / gone) is invalid (<c>FedSrv.CPP:13387,13407</c>).</item>
/// <item><c>pmodel-&gt;GetObjectType() == OT_ship</c> — the anchor hull must carry
/// <c>c_habmIsRipcordTarget</c>, or <c>c_habmIsRipcordTarget |
/// c_habmIsLtRipcordTarget</c> when the ripper's own hull has
/// <c>c_habmCanLtRipcord</c> (<c>FedSrv.CPP:13390-13402,13410-13422</c>). Any
/// non-ship anchor (station, probe) is otherwise legal.</item>
/// </list>
/// <para><b>Abort side effects</b> stay with the caller: native clears the
/// ripper's <c>droneRipMaskIGC</c> state bit and broadcasts
/// <c>RIPCORD_ABORTED</c> when the answer is false (<c>FedSrv.CPP:13425-13435</c>);
/// the port's sector does the same in its false branch (<c>Sector</c>'s
/// <c>_pendingRipcordAborts</c> → <c>Server</c>'s sector-wide broadcast).</para>
/// </remarks>
public static class RipcordRules
{
    /// <summary>
    /// Whether the anchor may still be ripped to this tick — the exact native
    /// <c>ContinueRipcord</c> boolean (<c>src/FedSrv/FedSrv.CPP:13379-13437</c>).
    /// A null/foreign argument (a marker that is not a port model) keeps the
    /// native interface default: allowed.
    /// </summary>
    public static bool AnchorStillLegal(IShipIgc? pship, IModelIgc? anchor)
    {
        // A non-port marker (the interface's documented default is `true`,
        // igc.h:4866) keeps the native base-site answer.
        if (pship is not Ship ship) return true;
        // No resolved anchor object: native's pmodel->GetCluster() == NULL leg
        // (a NULL m_pmodelRipcord never reaches the callback at all).
        if (anchor is null) return false;
        if (ship.Mission is not { } mission) return true;

        // FedSrv.CPP:13385/13405 — a flag carrier's ripcord is aborted.
        if (ship.Flag is not null) return false;

        bool Friendly(short? side) => side is not null && (side == ship.SideId ||
            (mission.AllowAlliedRip && ship.IsFriendlySide(side)));

        switch (anchor)
        {
            // FedSrv.CPP:13390-13402 (allied branch) / 13410-13422 (regular):
            // a ship anchor must be a live, flying, friendly ripcord target hull.
            case Ship target:
                if (target.Dead || target.StationId is not null || target.Sector is null) return false;
                if (!Friendly(target.SideId)) return false;
                short mask = ship.HullType.HasCapability(DataHullTypeIGC.HabmCanLtRipcord)
                    ? (short)(DataHullTypeIGC.HabmIsRipcordTarget | DataHullTypeIGC.HabmIsLtRipcordTarget)
                    : DataHullTypeIGC.HabmIsRipcordTarget;
                return target.HullType.HasCapability(mask);

            // FedSrv.CPP:13387/13407 + the `else rc = true` tail (:13403/13423):
            // a non-ship anchor only needs to be present and friendly (the full
            // activation/expiry horizon belongs to FindRipcordModel, not to every
            // subsequent tick).
            case Probe probe:
                return Friendly(probe.SideId);
            case Station station:
                return Friendly(station.SideId);
            case DockStation dockStation:                       // data-only station fixtures
                return Friendly(dockStation.Station.SideId);

            default:
                return true;
        }
    }
}
