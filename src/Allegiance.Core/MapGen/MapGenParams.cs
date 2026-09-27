namespace Allegiance.Core.MapGen;

/// <summary>
/// P397 — the mission parameters a map generator consumes, the port of the
/// subset of the native <c>MissionParams</c> that <c>ImapMakerIGC::Create</c>
/// and the shared <c>CmapMakerIGC::GenerateMission</c> algorithm actually read
/// (<c>mapmakerigc.cpp:340-483</c>, <c>common.cpp:2963</c>). Held separately
/// from <see cref="Allegiance.Core.Model.Mission.Assemble"/>'s
/// <see cref="Allegiance.Core.Model.SideSpec"/> list (which supplies the
/// per-team civilizations and names) — the generator needs the <em>counts</em>
/// and the per-side <c>rgCivID</c> to derive how many team/neutral clusters and
/// what each home station type is, while assembly consumes resolved sides.
/// </summary>
public sealed class MapGenParams
{
    /// <summary>Which topology to generate. Defaults to the only implemented one.</summary>
    public MapGenType Type { get; init; } = MapGenType.SingleRing;

    /// <summary><c>MissionParams.nTeams</c> — the number of sides.</summary>
    public int Teams { get; init; }

    /// <summary><c>MissionParams.rgCivID[]</c> — per-side civilization id (index
    /// into <see cref="StaticCore.Civilizations"/>). Length must be &gt;= Teams.</summary>
    public int[] CivIds { get; init; } = [];

    /// <summary><c>MissionParams.iMapSize</c> (KGJV extension) — 0 ⇒ one team
    /// cluster per team, non-zero ⇒ two (<c>mapmakerigc.cpp:883</c>).</summary>
    public int MapSize { get; init; }

    // ── per-sector population counts (MissionParams → CMapData getters) ──
    /// <summary>Home-sector He3 mineable asteroid count (<c>nPlayerSectorMineableAsteroids</c>).</summary>
    public short PlayerSectorMineableAsteroids { get; init; } = 10;
    /// <summary>Home-sector special asteroid count (<c>nPlayerSectorSpecialAsteroids</c>).</summary>
    public short PlayerSectorSpecialAsteroids { get; init; } = 2;
    /// <summary>Home-sector generic/buildable asteroid count (<c>nPlayerSectorAsteroids</c>).</summary>
    public short PlayerSectorAsteroids { get; init; } = 10;
    /// <summary>Home-sector treasure count (<c>nPlayerSectorTreasures</c>) — parsed
    /// for completeness but treasure <em>creation</em> is deferred (see
    /// <c>MissionMapGenerator</c>: the port's map universe has no treasure list).</summary>
    public short PlayerSectorTreasures { get; init; }

    /// <summary>Neutral-sector He3 mineable asteroid count (<c>nNeutralSectorMineableAsteroids</c>).</summary>
    public short NeutralSectorMineableAsteroids { get; init; } = 8;
    /// <summary>Neutral-sector special asteroid count (<c>nNeutralSectorSpecialAsteroids</c>).</summary>
    public short NeutralSectorSpecialAsteroids { get; init; } = 2;
    /// <summary>Neutral-sector generic/buildable asteroid count (<c>nNeutralSectorAsteroids</c>).</summary>
    public short NeutralSectorAsteroids { get; init; } = 8;
    /// <summary>Neutral-sector treasure count (<c>nNeutralSectorTreasures</c>) — deferred, same as above.</summary>
    public short NeutralSectorTreasures { get; init; }

    /// <summary><c>MissionParams.fHe3Density</c> — scales the per-map He3 total.</summary>
    public float He3Density { get; init; } = 1.0f;

    /// <summary><c>MissionParams.IsFlagsGame()</c> — unused this task (flag pedestal
    /// generation deferred with treasures). Parsed for config completeness.</summary>
    public bool FlagsGame { get; init; }

    /// <summary><c>MissionParams.IsArtifactsGame()</c> — unused this task (see above).</summary>
    public bool ArtifactsGame { get; init; }

    /// <summary><c>MissionParams.bShowHomeSector</c> — retained for fidelity; the
    /// port's visibility is dynamic per-tick (P132), so it has no static-reveal
    /// analogue and is not acted upon (documented in the P397 report).</summary>
    public bool ShowHomeSector { get; init; } = true;

    /// <summary><c>MissionParams.bAllowAlliedViz</c> — see <see cref="ShowHomeSector"/>.</summary>
    public bool AllowAlliedViz { get; init; }

    /// <summary><c>MissionParams.bShowMap</c> — see <see cref="ShowHomeSector"/>.</summary>
    public bool ShowMap { get; init; }
}
