using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P191 — pure-domain proofs of the <b>conquest/territory win conditions</b>:
/// the <see cref="Mission.RecomputeConquestAndTerritory"/> computation (the port
/// of <c>CFSMission::CheckForVictoryByStationKill</c>, <c>fsmission.cpp:3568-3710</c>)
/// and the <see cref="Wire.GameState"/> (155) codec. The live loopback
/// (<see cref="ConquestTerritoryLiveTests"/>) proves the full capture →
/// GameState → GameOver chain over the wire; this file pins the exact algorithm
/// and wire layout deterministically.
/// </summary>
/// <remarks>
/// Missions are assembled from the real <c>PCore001.igc</c> core + the real
/// <c>XXV_simple.igc</c> map, which (verified this task) carries exactly two
/// stations whose types resolve with <see cref="DataStationTypeIGC.SabmFlag"/>:
/// <c>#1051</c> (side 0) in cluster 1053 'Ceto' and <c>#1052</c> (side 1) in
/// cluster 1061 'Quaoar'; the other 11 clusters have zero stations. Synthetic
/// stations are added via <see cref="Mission.PlaceStation"/> to set up the
/// exact ownership/unanimity scenarios under test.
/// </remarks>
public sealed partial class ConquestTerritoryTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Mission Assembly()
    {
        var core = Core();
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));
        var sides = new[]
        {
            new SideSpec(0, "Alpha", 1),
            new SideSpec(1, "Bravo", 3),
        };
        var m = Mission.Assemble(core, map, sides, new RecordingIgcSite());
        m.Stage = MissionStage.Started;              // native gate: only Started/Starting recompute
        return m;
    }

    /// <summary>A synthetic station of the given <paramref name="side"/> with the
    /// given <c>SabmCapabilities</c>, added to <paramref name="sectorId"/> via
    /// <see cref="Mission.PlaceStation"/>. A real core station type is
    /// <c>with</c>-modified so the test controls the capability bits exactly.</summary>
    private static Station AddStation(Mission m, short sectorId, short side, int id, short sabm)
    {
        var core = m.Core;
        var baseType = core.StationTypes.Values.First();
        var type = baseType with { SabmCapabilities = sabm };
        var sector = m.Sectors.First(s => s.Id == sectorId);
        var data = new DataStationIGC(
            Position: [0f, 0f, -500f], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: sectorId, SideId: side, StationId: (short)id,
            StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: $"SYN_{id}");
        var st = new Station(data, type);
        m.PlaceStation(st, sector);
        return st;
    }

    // ── conquest ──────────────────────────────────────────────────────────

    /// <summary>⭐ Conquest recompute sets each side's <see cref="Side.ConquestPercent"/>
    /// from its share of <c>Flag</c>-capable stations (the real map contributes
    /// #1051 side0 + #1052 side1, both Flag → 50/50), and a side crossing the 65%
    /// threshold is declared the winner — the port of
    /// <c>fsmission.cpp:3581-3625</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Conquest_percent_updates_and_crosses_threshold_declares_winner()
    {
        Require.Artwork();
        var m = Assembly();
        m.Goals.ConquestPercentage = 65;

        // Baseline: the two real Flag stations → 50/50, no winner.
        var (w0, c0) = m.RecomputeConquestAndTerritory(null);
        Assert.Null(w0);
        Assert.True(c0);                                    // 0 → 50 changed
        Assert.Equal((byte)50, m.Sides[0].ConquestPercent);
        Assert.Equal((byte)50, m.Sides[1].ConquestPercent);

        // Add a third Flag station owned by side 0 (in empty cluster 1051):
        // side0 now owns 2/3 = 66% ≥ 65% → side0 wins.
        AddStation(m, 1051, 0, 7701, DataStationTypeIGC.SabmFlag);
        var (w1, c1) = m.RecomputeConquestAndTerritory(null);
        Assert.True(c1);
        Assert.NotNull(w1);
        Assert.Equal((short)0, w1!.Id);
        Assert.Equal((byte)66, m.Sides[0].ConquestPercent); // 100*2/3 = 66
        Assert.Equal((byte)33, m.Sides[1].ConquestPercent);
    }

    /// <summary>⭐ The <c>excluded</c> parameter removes exactly that station from
    /// the conquest total (the kill-path <c>pstationKilled</c> exclusion,
    /// <c>fsmission.cpp:3596</c>): excluding the sole side0 Flag station leaves
    /// only side1's, so side1 hits 100%.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Conquest_excludes_the_just_killed_station_from_the_total()
    {
        Require.Artwork();
        var m = Assembly();
        m.Goals.ConquestPercentage = 65;

        var side0Flag = m.Sectors.First(s => s.Id == 1053).StationRuntimes.First();  // #1051 side0
        var (w, _) = m.RecomputeConquestAndTerritory(side0Flag);
        Assert.Equal((short)1, w!.Id);                      // only side1's station remains → 100%
        Assert.Equal((byte)0, m.Sides[0].ConquestPercent);
        Assert.Equal((byte)100, m.Sides[1].ConquestPercent);
    }

    // ── territory ─────────────────────────────────────────────────────────

    /// <summary>⭐ A cluster whose stations are all one side counts for that side
    /// (Ceto → side0, Quaoar → side1), and the eleven EMPTY clusters count for
    /// NOBODY — the proof that zero-station clusters contribute nothing
    /// (<c>fsmission.cpp:3647-3653</c>). Threshold (13 clusters @50%) = 6, so no
    /// winner here.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Territory_counts_unanimous_clusters_and_ignores_empty_ones()
    {
        Require.Artwork();
        var m = Assembly();
        m.Goals.TerritoryPercentage = 50;

        var (w, c) = m.RecomputeConquestAndTerritory(null);
        Assert.Null(w);                                     // 1 < 6 threshold
        Assert.True(c);                                     // 0 → counts changed
        Assert.Equal((byte)1, m.Sides[0].TerritoryCount);   // only Ceto (unanimous side0)
        Assert.Equal((byte)1, m.Sides[1].TerritoryCount);   // only Quaoar (unanimous side1)
        // Not 2/12: the 11 empty clusters contributed nothing.
    }

    /// <summary>⭐ A cluster with stations from TWO different sides counts for
    /// NOBODY — the strict unanimous-ownership rule, not a majority. Adding a
    /// side1 station into Ceto (which held only side0's #1051) makes it mixed, so
    /// side0 loses that territory while side1 keeps its own Quaoar.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Territory_mixed_ownership_cluster_counts_for_nobody()
    {
        Require.Artwork();
        var m = Assembly();
        m.Goals.TerritoryPercentage = 50;

        // Baseline: side0 owns Ceto (unanimous), side1 owns Quaoar.
        m.RecomputeConquestAndTerritory(null);
        Assert.Equal((byte)1, m.Sides[0].TerritoryCount);

        // Drop a side1 station into Ceto → mixed ownership → Ceto counts for nobody.
        AddStation(m, 1053, 1, 7711, 0);
        m.RecomputeConquestAndTerritory(null);
        Assert.Equal((byte)0, m.Sides[0].TerritoryCount);   // lost Ceto (mixed)
        Assert.Equal((byte)1, m.Sides[1].TerritoryCount);   // still owns Quaoar
    }

    /// <summary>⭐ The unanimous test is a strict full-cluster test: a cluster
    /// owned by one side counts even when that side has only ONE station there —
    /// unanimity is about no dissent, not a quorum.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Territory_single_station_cluster_counts_for_its_side()
    {
        Require.Artwork();
        var m = Assembly();
        m.Goals.TerritoryPercentage = 50;

        // Put a lone side0 station into empty cluster 1051 (Arrokoth): it is
        // unanimous (only itself) → side0's count rises to 2 (Ceto + Arrokoth).
        AddStation(m, 1051, 0, 7721, 0);
        var (w, _) = m.RecomputeConquestAndTerritory(null);
        Assert.Equal((byte)2, m.Sides[0].TerritoryCount);
        Assert.Equal((byte)1, m.Sides[1].TerritoryCount);
        Assert.Null(w);                                     // 2 < 6 threshold
    }

    // ── default-disabled ──────────────────────────────────────────────────

    /// <summary>⭐ With both <see cref="Mission.ConquestEnabled"/> and
    /// <see cref="Mission.TerritoryEnabled"/> false (the default), the recompute
    /// is a complete no-op: no winner, no change flag, and every side's fields
    /// stay 0 — the regression guarantee that existing missions/tests (which
    /// never set these flags) are unaffected.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Both_flags_default_false_yields_no_change_and_no_winner()
    {
        Require.Artwork();
        var m = Assembly();
        Assert.False(m.ConquestEnabled);
        Assert.False(m.TerritoryEnabled);

        AddStation(m, 1051, 0, 7731, DataStationTypeIGC.SabmFlag);
        var (w, c) = m.RecomputeConquestAndTerritory(null);
        Assert.Null(w);
        Assert.False(c);
        Assert.All(m.Sides, s => Assert.Equal((byte)0, s.ConquestPercent));
        Assert.All(m.Sides, s => Assert.Equal((byte)0, s.TerritoryCount));
    }

    /// <summary>⭐ A mission not in <c>Started</c>/<c>Starting</c> never recomputes
    /// (the native stage gate, <c>fsmission.cpp:3575-3578</c>) — the idempotency
    /// guard that stops an Over mission from re-broadcasting.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Non_started_mission_never_recomputes()
    {
        Require.Artwork();
        var m = Assembly();
        m.Goals.ConquestPercentage = 65;
        m.Stage = MissionStage.NotStarted;

        AddStation(m, 1051, 0, 7741, DataStationTypeIGC.SabmFlag);
        var (w, c) = m.RecomputeConquestAndTerritory(null);
        Assert.Null(w);
        Assert.False(c);
        Assert.All(m.Sides, s => Assert.Equal((byte)0, s.ConquestPercent));
    }

    // ── GameState(155) codec ──────────────────────────────────────────────

    /// <summary>⭐ GAME_STATE(155) round-trips the four per-side arrays, and the
    /// <c>nFlags</c>/<c>nArtifacts</c> arrays are ALWAYS zero (native never sets
    /// them — see the codec remarks). Layout: header(4) + 4 byte-arrays × 6 =
    /// 28 bytes, no var items, no padding.</summary>
    [Fact]
    public void GameState_round_trips_and_nflags_nartifacts_are_zero()
    {
        Assert.Equal(28, GameState.FixedSize);
        Assert.Equal((ushort)155, GameState.Fmid);

        var msg = new GameState();
        msg.Conquest[0] = 66; msg.Conquest[1] = 33;
        msg.Territory[0] = 2; msg.Territory[1] = 1;
        // NFlags/NArtifacts intentionally left all-zero.

        var d = GameState.Decode(msg.Encode());
        Assert.Equal(GameState.Fmid, d.GetFmid());
        Assert.Equal((byte)66, d.Conquest[0]);
        Assert.Equal((byte)33, d.Conquest[1]);
        Assert.Equal((byte)2, d.Territory[0]);
        Assert.Equal((byte)1, d.Territory[1]);
        Assert.All(d.NFlags, b => Assert.Equal((byte)0, b));       // dead weight in native
        Assert.All(d.NArtifacts, b => Assert.Equal((byte)0, b));
    }

    /// <summary>⭐ Pin the exact wire layout offsets of GAME_STATE(155): header(4)
    /// then conquest[6]@4, territory[6]@10, nFlags[6]@16, nArtifacts[6]@22.</summary>
    [Fact]
    public void GameState_layout_pins_offsets()
    {
        var msg = new GameState();
        for (int i = 0; i < GameState.MaxSides; i++) { msg.Conquest[i] = (byte)(10 + i); msg.Territory[i] = (byte)(20 + i); }
        msg.NFlags[2] = 99; msg.NArtifacts[3] = 77;   // set them; Encode must STILL send them (round-trip preserves)
        byte[] b = msg.Encode();

        Assert.Equal(b.Length, BinaryPrimitives_ReadU16(b, 0));  // cbmsg
        Assert.Equal(155, BinaryPrimitives_ReadU16(b, 2));       // fmid
        for (int i = 0; i < 6; i++) Assert.Equal((byte)(10 + i), b[4 + i]);    // conquest @4
        for (int i = 0; i < 6; i++) Assert.Equal((byte)(20 + i), b[10 + i]);  // territory @10
        Assert.Equal((byte)99, b[16 + 2]);                                       // nFlags @16
        Assert.Equal((byte)77, b[22 + 3]);                                       // nArtifacts @22
    }

    /// <summary>⭐ <see cref="GameState.ForMission"/> fills conquest/territory from
    /// each real side in side-id order and leaves nFlags/nArtifacts all-zero.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GameState_ForMission_fills_sides_and_zeroes_flags()
    {
        Require.Artwork();
        var m = Assembly();
        m.Goals.ConquestPercentage = 65;
        m.RecomputeConquestAndTerritory(null);          // side0=50, side1=50

        var gs = GameState.ForMission(m);
        Assert.Equal((byte)50, gs.Conquest[0]);
        Assert.Equal((byte)50, gs.Conquest[1]);
        Assert.Equal((byte)0, gs.Conquest[2]);          // no side 2
        Assert.Equal((byte)0, gs.Territory[0]);
        Assert.All(gs.NFlags, b => Assert.Equal((byte)0, b));
        Assert.All(gs.NArtifacts, b => Assert.Equal((byte)0, b));
    }

    private static ushort BinaryPrimitives_ReadU16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
}
