namespace Allegiance.Core.Model;

/// <summary>
/// The derived (non-wire) predicates the tech-tree development slice computes
/// from a decoded <see cref="DataDevelopmentIGC"/> — the port of native
/// members that are <b>not</b> part of the decoded struct but computed at
/// runtime on <c>IdevelopmentIGC</c>.
/// </summary>
public static class DevelopmentLogic
{
    /// <summary>
    /// Whether a development is <b>tech-only</b> — it grants a tech-tree
    /// bitmask but no attribute delta. This is the exact native derivation in
    /// the <c>CdevelopmentIGC</c> constructor (<c>src/Igc/developmentigc.cpp:32-47</c>):
    /// <code>
    /// if (m_data.developmentID == c_didTeamMoney)
    ///     m_techOnly = false;                 // winning the game is not "tech only"
    /// else
    /// {
    ///     m_techOnly = true;
    ///     for (int i = 0; i &lt; c_gaMax; i++)
    ///         if (m_data.gas.GetAttribute(i) != 1.0f)
    ///         { m_techOnly = false; break; }
    /// }
    /// </code>
    /// The <c>c_didTeamMoney</c> economic-victory special case is out of scope
    /// for this task (stated in the brief and the stage report), so the
    /// derivation here is the else-branch: a development is tech-only exactly
    /// when every one of its 25 <c>Gas</c> factors equals <c>1.0</c>. This is
    /// deliberately computed from the decoded wire data (the <c>Gas</c> array),
    /// not stored — native does not carry <c>m_techOnly</c> on the wire either.
    /// </summary>
    public static bool IsTechOnly(DataDevelopmentIGC dev)
    {
        foreach (var g in dev.Gas)
            if (g != 1.0f) return false;
        return true;
    }

    /// <summary>
    /// Whether a development is <b>obsolete</b> — a tech-only development whose
    /// granted effect techs the side already fully holds. The port of native
    /// <c>IdevelopmentIGC::IsObsolete</c> (<c>src/Igc/developmentigc.h:110-113</c>):
    /// <code>
    /// return m_techOnly &amp;&amp; (m_data.ttbmEffects &lt;= ttbm);
    /// </code>
    /// An obsolete development grants the side nothing new, so the native
    /// <c>AdjustBuckets</c> marks its bucket complete as a no-op
    /// (<c>sideigc.h:629-633</c>) instead of running the completion flow; this
    /// port mirrors that by <b>omitting</b> the obsolete development from a
    /// side's bucket catalog entirely (see <see cref="BucketCatalog.ForSide"/>),
    /// so it can never be funded/completed and never re-applies or re-broadcasts
    /// anything.
    /// </summary>
    public static bool IsObsolete(DataDevelopmentIGC dev, TechTreeBitMask developmentTechs)
    {
        if (!IsTechOnly(dev)) return false;
        return TechTreeBitMask.FromBytes(dev.TtbmEffects).IsSubsetOf(developmentTechs);
    }
}
