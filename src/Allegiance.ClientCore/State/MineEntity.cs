using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

/// <summary>
/// A live deployed mine in the client's world model — one per real mine,
/// keyed by <see cref="MineId"/> (mines carry a real ObjectID on the wire,
/// <c>DataMineExport.MineId</c>). Like a <see cref="ProbeEntity"/>, a mine is a
/// <b>static</b> object: it never moves (it sits exactly where it was laid —
/// native <c>CmineIGC</c> has no position integration, so the EXPORT's
/// <see cref="Position"/> is final for its lifetime). Populated from the
/// creation <c>FM_S_EXPORT(26)</c>/<c>OT_mine</c> broadcast and removed by the
/// server's <c>FM_S_MINE_DESTROYED(84)</c> (sent on detonation or lifespan
/// expiry, <c>Server.BroadcastMineDestroyed</c>, <c>Server.cs:10312</c>), so a
/// <see cref="MineEntity"/>'s presence in the world mirrors a live mine's
/// survival — the client-side substrate P291's renderer reads.
/// </summary>
/// <remarks>
/// <para><b>Why <see cref="MinetypeId"/> rides along (state plumbing a probe
/// didn't need).</b> Unlike probes, whose single shared <c>utl02</c> model
/// represents every deployed probe (P290), a mine's rendered <em>extent</em>
/// depends on its type's real radius: native loads the mine with
/// <c>LoadMine(textureName, 1.0f, radius, iconName, ...)</c>
/// (<c>src/Igc/mineigc.cpp:103</c>) and scatters its decal field within a
/// sphere of that radius (<c>MineFieldGeo</c>, <c>src/effect/efgeo.cpp:15-35</c>).
/// The wire <em>does</em> carry the mine type id (<c>DataMineExport.MinetypeId</c>),
/// so the renderer resolves the radius from the static core's
/// <c>DataMineTypeIGC.MineRadius</c> (see <c>MineModelSource</c>). A probe needs
/// no such field because every probe type shares one fixed model/radius.</para>
/// <para><b>Radius nuance (verified this session).</b> Native C++ resolves
/// <c>dataMineType-&gt;radius</c> on a <c>DataMineTypeIGC*</c> to the
/// <b>derived</b> member <c>DataMineTypeIGC::radius</c> (igc.h:1992) — which C++
/// member-shadowing hides behind the inherited <c>DataObjectIGC::radius</c>
/// (igc.h:1907). This port's decoder names that derived radius
/// <c>DataMineTypeIGC.MineRadius</c> (offset 464), while its <c>Radius</c>
/// (offset 16) is the shadowed, unused <c>DataObjectIGC::radius</c> — garbage
/// (-107374180) in every real corpus record. So the visual/operational radius is
/// <c>MineRadius</c> (75–250 in the corpus), NOT the base <c>Radius</c>.</para>
/// </remarks>
public sealed class MineEntity
{
    /// <summary>The mine's ObjectID (<c>MineId</c> = short, the key).</summary>
    public short MineId;

    /// <summary>The side that owns this mine (<c>DataMineExport.SideId</c>),
    /// used to tint the decal field friendly/enemy (same convention as
    /// <see cref="ProbeEntity.SideId"/> / the station renderer).</summary>
    public short SideId;

    /// <summary>The laying ship's id (<c>DataMineExport.LauncherId</c>, the
    /// <c>m_launcher</c>) — <c>NA</c>(-1) if none. Not currently used by the
    /// renderer, carried for parity with <see cref="ProbeEntity"/> and the
    /// server's same-side audience accounting.</summary>
    public short LauncherShipId;

    /// <summary>The mine's type id (<c>DataMineExport.MinetypeId</c>) — the
    /// <c>DataMineTypeIGC.ExpendableTypeId</c>. The renderer resolves the real
    /// <c>MineRadius</c> from it via the static core.</summary>
    public short MinetypeId;

    /// <summary>The sector (cluster) this mine lives in — the draw loop's
    /// sector filter key, exactly like probes/warps/asteroids.</summary>
    public short ClusterId;

    /// <summary>The mine's position (<c>DataMineExport.P0</c>) — fixed for the
    /// mine's whole life (a mine never moves; native has no position update,
    /// and <c>Server.BroadcastMineDestroyed</c> explicitly re-carries the same
    /// deployment position).</summary>
    public Vector3 Position;

    /// <summary>P574 — the mine's current strength fraction (native
    /// <c>m_fraction</c>, <c>mineigc.h:127,175</c>), defaulting to 1.0 (a fresh
    /// field). Updated by the server's <c>MINE_STRENGTH</c> wire update on every
    /// hit, and the renderer thins the <c>MineFieldGeometry</c> decal cloud to
    /// <c>1 + int(strength·radius²/100)</c> decals exactly as native
    /// <c>SetMineStrength</c>/<c>SetCount</c> does (<c>efgeo.cpp:21-24,43-51</c>)
    /// — the visible \"the field wears down\" half of a mine popping.</summary>
    public float Strength = 1f;

    /// <summary>
    /// P786 — the mine's native <c>m_time0</c> lay instant, in server mission
    /// seconds (<c>DataMineExport.Time0</c> is the wire's uint server
    /// millisecond tick; the export sets it from the live mine at creation,
    /// <c>mineigc.cpp:167</c> <c>pdme-&gt;time0 = m_time0</c>, with
    /// <c>m_time0 = now</c> at construction <c>mineigc.cpp:40</c>). This is the
    /// value native's <c>GetTimeFraction</c> needs (<c>mineigc.h:183-191</c>:
    /// <c>(m_timeExpire − now) / (m_timeExpire − m_time0)</c>,
    /// <c>m_timeExpire = m_time0 + lifespan</c>, <c>mineigc.cpp:113</c>) — the
    /// radar mine contact's lifetime fill bar. Interpreted against
    /// <see cref="ClientWorld.SimulationTimeSeconds"/>.
    /// </summary>
    public double Time0ServerSeconds;
}
