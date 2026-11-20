using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>Spectator sector travel (Domingo 2026-11-20): the free camera
/// crosses alephs like a ship, Home/End step through sectors, a picked sector
/// frames its station.</summary>
public sealed class SpectatorSectorTravelTests
{
    private static DataWarpIGC Warp(short id, short dest, short cluster, Vector3 pos, Vector3 fwd, short radius = 100)
        => new(id, dest, radius, "", "", "", new[] { pos.X, pos.Y, pos.Z }, new[] { fwd.X, fwd.Y, fwd.Z }, new float[9], 0f, cluster);

    private static Dictionary<short, DataWarpIGC> Pair() => new()
    {
        [1] = Warp(1, 2, 10, new Vector3(1000, 0, 0), Vector3.UnitX),
        [2] = Warp(2, 1, 20, new Vector3(-500, 200, 0), Vector3.UnitZ),
    };

    [Fact]
    public void Camera_inside_an_aleph_comes_out_of_the_linked_aleph_facing_out()
    {
        Assert.True(SpectatorSectorTravel.TryCrossAleph(new Vector3(1050, 0, 0), 10, Pair(), out var arrival));
        Assert.Equal((short)20, arrival.SectorId);
        Assert.Equal(Vector3.UnitZ, arrival.Forward);
        float expected = 100 + SpectatorSectorTravel.ExitClearance;
        Assert.Equal(new Vector3(-500, 200, expected), arrival.Eye);
        // The exit point is outside the destination aleph: no bounce back.
        Assert.False(SpectatorSectorTravel.TryCrossAleph(arrival.Eye, 20, Pair(), out _));
    }

    [Fact]
    public void Camera_near_but_outside_an_aleph_or_in_another_sector_stays()
    {
        Assert.False(SpectatorSectorTravel.TryCrossAleph(new Vector3(1200, 0, 0), 10, Pair(), out _));
        Assert.False(SpectatorSectorTravel.TryCrossAleph(new Vector3(1000, 0, 0), 20, Pair(), out _));
    }

    [Theory]
    [InlineData(5, 1, 7)]
    [InlineData(9, 1, 3)]     // wraps
    [InlineData(3, -1, 9)]    // wraps backwards
    [InlineData(-1, 1, 3)]    // not viewing a sector yet: first
    [InlineData(-1, -1, 9)]   // ... or last
    public void Sector_keys_step_by_id_and_wrap(short current, int step, short expected)
        => Assert.Equal(expected, SpectatorSectorTravel.CycleSector(new short[] { 9, 3, 7, 5 }, current, step));

    [Fact]
    public void Picked_sector_frames_its_station_else_its_aleph()
    {
        var stations = new Dictionary<short, DataStationIGC>();
        var arrival = SpectatorSectorTravel.ArrivalView(20, stations, Pair());
        var target = new Vector3(-500, 200, 0);
        Assert.Equal(20, arrival.SectorId);
        Assert.Equal(SpectatorSectorTravel.ArrivalDistance, Vector3.Distance(arrival.Eye, target), 1);
        Assert.True(Vector3.Dot(arrival.Forward, Vector3.Normalize(target - arrival.Eye)) > 0.999f);
    }

    [Fact]
    public void Yaw_pitch_round_trips_through_the_free_camera_basis()
    {
        var cam = new FreeCam();
        var want = Vector3.Normalize(new Vector3(0.3f, -0.4f, -0.8f));
        (cam.Yaw, cam.Pitch) = SpectatorSectorTravel.YawPitch(want);
        Assert.True(Vector3.Dot(cam.Basis().Forward, want) > 0.9999f);
    }

    [Fact]
    public void Going_to_a_sector_parks_the_free_camera_there()
    {
        var camera = new Allegiance.SinglePlayer.SkirmishCamera();
        camera.GoToSector(42);
        Assert.Equal(Allegiance.SinglePlayer.SkirmishCameraMode.Free, camera.Mode);
        Assert.Equal((short)42, camera.SectorId);
        Assert.Null(camera.ShipId);
    }
}
