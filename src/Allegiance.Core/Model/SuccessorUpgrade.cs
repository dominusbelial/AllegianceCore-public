namespace Allegiance.Core.Model;

/// <summary>
/// P488 — the port of the native "successor upgrade" family: when a side has
/// out-teched an item (station type, hull, or part), the thing you actually
/// get/build is the side's best <em>available</em> version, per native
/// <c>GetSuccessor…</c>. The port had been decoding
/// <see cref="DataHullTypeIGC.SuccessorHullId"/> /
/// <see cref="DataPartTypeIGC.SuccessorPartId"/> /
/// <see cref="DataStationTypeIGC.SuccessorStationTypeId"/> since the data pass
/// but never consuming them. This is the single shared implementation so the
/// server's buy gate (<c>ApplyLoadout</c>), the station-build completion
/// (<c>Sector.CompleteBuild</c>) and the client shop's obsolete filter all walk
/// the same chains with the same native semantics.
/// </summary>
public static class SuccessorUpgrade
{
    /// <summary>
    /// The port of native <c>Station::GetSuccessor(IbuyableIGC*)</c>
    /// (<c>stationIGC.h:398-433</c>) for hulls: starting from
    /// <paramref name="hull"/>, walk the whole successor chain
    /// (<c>GetSuccessorHullType</c>) — <b>even through items the station can't
    /// currently buy</b> — and return the <em>furthest</em> item that
    /// <em>is</em> buyable (native updates <c>pSuccessor</c> only when
    /// <c>CanBuy(pNext)</c>, but advances <c>pNext</c> unconditionally). So a
    /// side that has jumped tiers still reaches its best available hull. When
    /// the base itself is not buyable, or no buyable successor exists, the
    /// input is returned unchanged.
    /// </summary>
    public static DataHullTypeIGC HullSuccessor(
        StaticCore core, DataHullTypeIGC hull,
        byte[]? stationLocal, TechTreeBitMask sideTechs)
    {
        if (!TechTreeBitMask.RequiredTechsBuyable(hull.TtbmRequired, stationLocal, sideTechs))
            return hull;
        var result = hull;
        short? next = hull.SuccessorHullId;
        var guard = 0;
        while (next is { } id && id >= 0 && id != hull.HullId && ++guard <= 32768)
        {
            if (!core.HullTypes.TryGetValue(id, out var cand)) break;
            if (TechTreeBitMask.RequiredTechsBuyable(cand.TtbmRequired, stationLocal, sideTechs))
                result = cand;
            next = cand.SuccessorHullId;
        }
        return result;
    }

    /// <summary>
    /// The native <c>Station::GetSuccessor</c> for hulls: true when a buyable
    /// successor exists (i.e. <see cref="HullSuccessor"/> returns something
    /// other than the input). This is exactly native <c>IsObsolete(b)</c>
    /// (<c>stationIGC.h:394-396</c>), the term the hangar applies to hide a
    /// hull the side has out-teched (<c>WinTrek/load.cpp:1606,1819</c>).
    /// </summary>
    public static bool HullIsObsolete(
        StaticCore core, DataHullTypeIGC hull,
        byte[]? stationLocal, TechTreeBitMask sideTechs)
        => HullSuccessor(core, hull, stationLocal, sideTechs).HullId != hull.HullId;

    /// <summary>
    /// The native <c>Station::GetSuccessor</c> for parts (same walk as
    /// <see cref="HullSuccessor"/>, over <c>GetSuccessorPartType</c>). A
    /// launcher/dispenser part (built from <c>core.Launchers</c>) resolves its
    /// successor through the same launcher table the mountable form came from.
    /// </summary>
    public static DataPartTypeIGC PartSuccessor(
        StaticCore core, DataPartTypeIGC part,
        byte[]? stationLocal, TechTreeBitMask sideTechs)
    {
        if (!TechTreeBitMask.RequiredTechsBuyable(part.TtbmRequired, stationLocal, sideTechs))
            return part;
        var result = part;
        short? next = part.SuccessorPartId;
        var guard = 0;
        while (next is { } id && id >= 0 && id != part.PartId && ++guard <= 32768)
        {
            if (!TryResolvePart(core, id, out var cand)) break;
            if (TechTreeBitMask.RequiredTechsBuyable(cand.TtbmRequired, stationLocal, sideTechs))
                result = cand;
            next = cand.SuccessorPartId;
        }
        return result;
    }

    /// <summary>The native <c>Station::IsObsolete</c> for parts.</summary>
    public static bool PartIsObsolete(
        StaticCore core, DataPartTypeIGC part,
        byte[]? stationLocal, TechTreeBitMask sideTechs)
        => PartSuccessor(core, part, stationLocal, sideTechs).PartId != part.PartId;

    /// <summary>
    /// The port of native <c>IstationTypeIGC::GetSuccessorStationType(pside)</c>
    /// (<c>stationtypeigc.h:185-195</c>), used by native <c>BuildStation</c>
    /// (<c>FedSrv.CPP:11380</c>) to "allow for an upgrade to the station being
    /// available". Recurses into the direct successor while
    /// <c>succ.RequiredTechs ⊆ (thisStation.RequiredTechs | side.DevelopmentTechs)</c>
    /// — the side having this station implies it once had this station's
    /// required bits. Unlike the hull/part <see cref="HullSuccessor"/> walk,
    /// this advances <b>only</b> through the immediate successor (it does not
    /// skip a non-researchable intermediate).
    /// </summary>
    public static DataStationTypeIGC StationSuccessor(
        StaticCore core, DataStationTypeIGC st, TechTreeBitMask sideDev)
    {
        short succId = st.SuccessorStationTypeId;
        if (succId >= 0 && succId != st.StationTypeId &&
            core.StationTypes.TryGetValue(succId, out var succ) &&
            TechTreeBitMask.RequiredTechsBuyable(succ.TtbmRequired, st.TtbmRequired, sideDev))
        {
            return StationSuccessor(core, succ, sideDev);
        }
        return st;
    }

    private static bool TryResolvePart(StaticCore core, short id, out DataPartTypeIGC part)
    {
        if (core.PartTypes.TryGetValue(id, out part)) return true;
        return core.TryBuildExpendableLauncher(id, out part);
    }
}
