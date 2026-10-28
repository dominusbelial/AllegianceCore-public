using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using System.Buffers.Binary;

namespace Allegiance.Core.Tests;

/// <summary>
/// P363 — pure-domain proofs of the <b>ship side-coloured motion trail's data
/// foundation</b>: the real per-side colour (<see cref="SidePalette"/> — the
/// port of native <c>UpdateSides</c>'s <c>sideColors</c> table,
/// missionigc.cpp:3734-3746), that <see cref="Mission.Assemble"/> seeds each
/// side's <see cref="Side.ColorR..A"/> from it, that the endgame writer
/// (<see cref="GameOver.ForMission"/>) now emits those real colours instead of
/// the old neutral gray (making the existing <c>SideEndgameInfo.Color</c> wire
/// field truthful), and that <see cref="SideRoster"/> (211) carries the colour
/// to the client at join for a live in-flight ship.
/// </summary>
public sealed class ShipTrailSideColorTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Mission Assembly()
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));
        var sides = new[]
        {
            new SideSpec(0, "Alpha", 1),
            new SideSpec(1, "Bravo", 3),
        };
        return Mission.Assemble(core, map, sides, new RecordingIgcSite());
    }

    /// <summary>Native <c>UpdateSides</c> assigns each side its colour by side id
    /// from a fixed six-slot palette (missionigc.cpp:3734-3746); the port's
    /// <see cref="SidePalette"/> reproduces it exactly.</summary>
    [Theory]
    [InlineData(0, 188f / 255f, 160f / 255f, 0f)]
    [InlineData(1, 0f, 138f / 255f, 217f / 255f)]
    [InlineData(2, 156f / 255f, 016f / 255f, 102f / 255f)]
    [InlineData(5, 050f / 255f, 200f / 255f, 125f / 255f)]
    public void SidePalette_matches_native_table(short id, float r, float g, float b)
    {
        var (pr, pg, pb) = SidePalette.ColorFor(id);
        Assert.Equal(r, pr, 6);
        Assert.Equal(g, pg, 6);
        Assert.Equal(b, pb, 6);
    }

    [Fact]
    public void SidePalette_falls_back_to_neutral_gray_out_of_range()
    {
        var (r, g, b) = SidePalette.ColorFor(99);
        Assert.Equal(0.5f, r);
        Assert.Equal(0.5f, g);
        Assert.Equal(0.5f, b);
    }

    /// <summary>Assemble seeds each side's colour from the palette by side id —
    /// side 0 is the native Gold, side 1 the native Blue (the "bluish" side of
    /// Domingo's memory is exactly native's side-1 blue).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mission_assemble_seeds_side_colours_from_palette()
    {
        Require.Artwork();
        var m = Assembly();
        Assert.Equal(188f / 255f, m.Sides[0].ColorR, 6);   // Gold
        Assert.Equal(160f / 255f, m.Sides[0].ColorG, 6);
        Assert.Equal(138f / 255f, m.Sides[1].ColorG, 6);   // Blue
        Assert.Equal(217f / 255f, m.Sides[1].ColorB, 6);
        Assert.Equal(1f, m.Sides[1].ColorA);
    }

    /// <summary>⭐ GameOver.ForMission now writes the sides' REAL colours (the
    /// <c>pside->GetColor()</c>-sourced RGBA the <c>SideEndgameInfo.Color</c>
    /// wire field was always designed to carry, fsmission.cpp:3073), instead of
    /// the previous neutral-gray stand-in — generalising the same decode
    /// concept to real side colours.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GameOver_ForMission_writes_real_side_colours()
    {
        Require.Artwork();
        var m = Assembly();
        var go = GameOver.ForMission(m, winnerSideId: 0, reason: "Alpha won.");
        Assert.Equal(188f / 255f, go.Sides[0].R, 6);
        Assert.Equal(160f / 255f, go.Sides[0].G, 6);
        Assert.Equal(138f / 255f, go.Sides[1].G, 6);
        Assert.Equal(217f / 255f, go.Sides[1].B, 6);
    }

    /// <summary>SIDE_ROSTER(211) carries each side's RGBA colour (four floats,
    /// the native 16-byte zlib <c>Color</c>) to the client at join; the record
    /// grew 30 → 46 bytes and round-trips.</summary>
    [Fact]
    public void SideRoster_codec_round_trips_colours_and_new_record_size()
    {
        Assert.Equal(46, SideRoster.RecordSize);

        var roster = new SideRoster
        {
            Sides =
            [
                new SideRoster.SideRecord(0, CivId: 1, PlayerCount: 2, "Alpha", 188f / 255f, 160f / 255f, 0f, 1f),
                new SideRoster.SideRecord(1, CivId: 3, PlayerCount: 1, "Bravo", 0f, 138f / 255f, 217f / 255f, 1f),
            ],
        };

        var back = SideRoster.Decode(roster.Encode());

        Assert.Equal(2, back.Sides.Length);
        Assert.Equal(188f / 255f, back.Sides[0].R, 6);
        Assert.Equal(160f / 255f, back.Sides[0].G, 6);
        Assert.Equal(138f / 255f, back.Sides[1].G, 6);
        Assert.Equal(217f / 255f, back.Sides[1].B, 6);
        Assert.Equal(1f, back.Sides[1].A);
        Assert.Equal("Bravo", back.Sides[1].Name);
        Assert.Equal((short)0, back.LobbyPlayerCount);   // not set -> 0
    }

    /// <summary>P864b — SIDE_ROSTER(211) is backward-compatible with pre-P864b
    /// recorded wire captures (battle replays): the roster's header grew 6 → 8
    /// bytes with the new lobbyPlayerCount, but the decoder sniffs the header
    /// size by exact length and reads an old 6-byte-header packet as a zero
    /// lobby (the pre-P864b format P729/P730 replay tests replay).</summary>
    [Fact]
    public void SideRoster_decodes_old_6byte_header_with_zero_lobby()
    {
        var roster = new SideRoster
        {
            Sides =
            [
                new SideRoster.SideRecord(0, CivId: 1, PlayerCount: 2, "Alpha"),
                new SideRoster.SideRecord(1, CivId: 3, PlayerCount: 1, "Bravo"),
            ],
        };
        // Build a pre-P864b packet: 6-byte header (cbmsg+fmid+nSides, NO lobby
        // field) followed by the n records — exactly what the old encoder and
        // the recorded battle-replay captures carry.
        var newEncoded = roster.Encode();                       // 8-byte header
        var oldBuf = new byte[6 + SideRoster.RecordSize * roster.Sides.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(oldBuf, (ushort)oldBuf.Length);  // cbmsg
        BinaryPrimitives.WriteUInt16LittleEndian(oldBuf.AsSpan(2), SideRoster.Fmid);
        BinaryPrimitives.WriteInt16LittleEndian(oldBuf.AsSpan(4), (short)roster.Sides.Length);
        newEncoded.AsSpan(8).CopyTo(oldBuf.AsSpan(6));           // the n records
        Assert.Equal(6 + SideRoster.RecordSize * 2, oldBuf.Length);

        var back = SideRoster.Decode(oldBuf);
        Assert.Equal(2, back.Sides.Length);
        Assert.Equal("Alpha", back.Sides[0].Name);
        Assert.Equal("Bravo", back.Sides[1].Name);
        Assert.Equal((short)0, back.LobbyPlayerCount);   // absent in old format
    }
}
