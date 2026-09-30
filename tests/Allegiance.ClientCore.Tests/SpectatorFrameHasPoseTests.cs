using System.Text.Json;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P440 — a ship whose sector is KNOWN but whose position is NOT must never
/// be rendered at the sector origin [0,0,0] (a phantom ship where nothing
/// real is). The chain, from source: <c>ClientWorld.Apply(ShipStatusMessage)</c>
/// sets <c>ShipEntity.SectorId</c> from SHIP_STATUS(90), which carries no pose;
/// for a ship the spectator has never received a pose update for (not in its
/// own cluster, so no HeavyShipsUpdate(176)/SingleShipUpdate(78)),
/// <c>ShipEntity.Latest</c> stays null while <c>SectorId</c> is real.
/// <c>SpectatorFrame.BuildTickFrame</c>'s <c>Vec</c> helper maps a null pose to
/// <c>[0,0,0]</c>, so the frame used to claim "this ship is at the origin of
/// sector N". P440 adds a <c>hasPose</c> flag to each tick-frame ship entry:
/// <c>false</c> exactly when there is no real pose (<c>Latest</c> null), so the
/// frontend can TRACK the ship (in <c>shipState</c> and the sector census)
/// without RENDERING the phantom. <c>ExtraShip</c> entries always carry a real
/// position, so they are unconditionally <c>hasPose:true</c>.
/// </summary>
public class SpectatorFrameHasPoseTests
{
    private const short MateId = 50;   // a same-side teammate whose pose we lack

    private static string Hull(short hullId) => hullId == 5 ? "Adv Scout" : $"hull{hullId}";

    private static ServerShipUpdate Pose(short id, float x) => ServerShipUpdate.FromValues(
        id, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 1u,
        new Vector3(x, 0f, 0f), Orientation.Identity, Vector3.Zero,
        turnRates: new[] { 0f, 0f, 0f },
        hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
        energyFrac: 1f, oreFrac: 0f, power: 0f);

    private static SetCluster SetClusterFor(short myId, short sectorId) => new()
    {
        SectorId = sectorId,
        Ship = Pose(myId, 0f),
        Cookie = 0x1,
    };

    private static ShipStatusMessage StatusFor(short shipId, short sectorId) => new()
    {
        ShipId = shipId,
        HullId = 5,
        SectorId = sectorId,
        StationId = -1,
        ParentId = -1,
        State = ShipStatusMessage.StateFlying,
        Detected = true,
    };

    private static SingleShipUpdate PoseUpdateFor(short shipId, float x) => new()
    {
        OtTarget = 0,
        OidTarget = shipId,
        Ship = Pose(shipId, x),
        BIsRipcording = false,
    };

    private static JsonElement? FindShip(string tickJson, short id)
    {
        using var doc = JsonDocument.Parse(tickJson);
        foreach (var s in doc.RootElement.GetProperty("ships").EnumerateArray())
            if (s.GetProperty("id").GetInt16() == id)
                return s.Clone();
        return null;
    }

    /// <summary>⭐ The bug's repro: SHIP_STATUS(90) sets a real SectorId but no
    /// pose update ever follows (the ship is outside the spectator's cluster),
    /// so <c>Latest</c> stays null and the old code emitted pos=[0,0,0] with a
    /// real sector — a phantom at the sector origin. After the fix the entry
    /// carries <c>hasPose:false</c>, which the frontend treats as track-but-
    /// don't-render.</summary>
    [Fact]
    public void SectorKnown_PoseUnknown_entry_is_hasPose_false_at_origin()
    {
        var world = new ClientWorld();
        world.Apply(SetClusterFor(myId: 42, sectorId: 3));
        // The mate's ship exists and reports sector 9, but we never see a pose.
        world.Apply(StatusFor(MateId, sectorId: 9));

        long counter = 0;
        string tick = SpectatorFrame.BuildTickFrame(world, Hull, simTime: 1.0, ref counter);

        var e = FindShip(tick, MateId);
        Assert.True(e.HasValue, "the sector-known mate must appear in the tick frame");
        Assert.False(e.Value.GetProperty("hasPose").GetBoolean(), "no pose update received -> hasPose must be false");
        // The coordinate is still the [0,0,0] Vec() fallback — it must NOT be
        // trusted for rendering while hasPose is false.
        var pos = e.Value.GetProperty("pos").EnumerateArray().Select(p => p.GetDouble()).ToArray();
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, pos);
        Assert.Equal((short)9, e.Value.GetProperty("realSector").GetInt16());
    }

    /// <summary>Once a real pose arrives (SINGLE_SHIP_UPDATE(78) / HeavyShipsUpdate),
    /// the same ship flips to <c>hasPose:true</c> with the real <c>pos</c> — the
    /// frontend's existing per-tick scene-membership re-check then renders it.</summary>
    [Fact]
    public void PoseUpdate_flips_entry_to_hasPose_true_with_real_pos()
    {
        var world = new ClientWorld();
        world.Apply(SetClusterFor(myId: 42, sectorId: 3));
        world.Apply(StatusFor(MateId, sectorId: 9));               // sector known, no pose yet
        world.Apply(PoseUpdateFor(MateId, x: 270.031f));           // now a real pose arrives

        long counter = 0;
        string tick = SpectatorFrame.BuildTickFrame(world, Hull, simTime: 1.0, ref counter);

        var e = FindShip(tick, MateId);
        Assert.True(e.HasValue);
        Assert.True(e.Value.GetProperty("hasPose").GetBoolean(), "a pose update must flip hasPose to true");
        var pos = e.Value.GetProperty("pos").EnumerateArray().Select(p => p.GetDouble()).ToArray();
        Assert.Equal(MathF.Round(270.031f, 2), (float)pos[0]);     // the REAL position, not [0,0,0]
    }

    /// <summary>An <c>ExtraShip</c> (the attach mode's opposite contingent,
    /// folded in from each enemy peer's own self-pose) always carries a real
    /// position, so it is unconditionally <c>hasPose:true</c>.</summary>
    [Fact]
    public void ExtraShip_entry_is_hasPose_true()
    {
        var world = new ClientWorld();
        world.Apply(SetClusterFor(myId: 42, sectorId: 3));
        var extra = new SpectatorFrame.ExtraShip(
            Id: 77, HullId: 5, SideId: 1, Position: new Vector3(-512f, 0f, 96f),
            Forward: Orientation.Identity.GetForward(), HullFraction: 1f, Alive: true, SectorId: 3);

        long counter = 0;
        string tick = SpectatorFrame.BuildTickFrame(world, Hull, simTime: 1.0, ref counter, new[] { extra });

        var e = FindShip(tick, 77);
        Assert.True(e.HasValue);
        Assert.True(e.Value.GetProperty("hasPose").GetBoolean(), "an ExtraShip always carries a real pose");
        var pos = e.Value.GetProperty("pos").EnumerateArray().Select(p => p.GetDouble()).ToArray();
        Assert.Equal((-512f), (float)pos[0]);
        Assert.Equal(96f, (float)pos[2]);
    }

    /// <summary>The flag is additive: every world-ship entry still carries the
    /// full classic schema (id/hull/side/pos/fwd/hp/alive/realSector), so the
    /// frontend's classic/explore rendering paths are untouched by the new
    /// field's presence.</summary>
    [Fact]
    public void PoseUnknown_entry_keeps_full_classic_schema()
    {
        var world = new ClientWorld();
        world.Apply(SetClusterFor(myId: 42, sectorId: 3));
        world.Apply(StatusFor(MateId, sectorId: 9));

        long counter = 0;
        string tick = SpectatorFrame.BuildTickFrame(world, Hull, simTime: 1.0, ref counter);

        var e = FindShip(tick, MateId);
        Assert.True(e.HasValue);
        foreach (var prop in new[] { "id", "hull", "side", "pos", "fwd", "hp", "alive", "realSector", "hasPose" })
            Assert.True(e.Value.TryGetProperty(prop, out _), $"entry must keep schema field '{prop}'");
    }
}
