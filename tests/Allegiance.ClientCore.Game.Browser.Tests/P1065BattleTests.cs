using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Allegiance.ClientCore.Browser.Assets;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1065BattleTests
{
    private static readonly DataHullTypeIGC Hull = default(DataHullTypeIGC) with
    { HullId = 1, ModelName = "fig21", Length = 24, Mass = 1, MaxTurnRates = [1, 1, 1], TurnTorques = [1, 1, 1], Hardpoints = [], PmEquipment = new short[8] };
    private static ServerShipUpdate Pose(short id) => ServerShipUpdate.FromModel(id,
        new Ship(id, Hull, new SimVec(100, 0, 0), SimVec.Zero, Orientation.Identity), 100);
    private static ClientWorld World()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = Pose(1) });
        world.Apply(new ExportShip { ShipId = 1, HullId = 1, SideId = 0, CharacterName = "Pilot" });
        world.Apply(new ExportShip { ShipId = 2, HullId = 1, SideId = 1, CharacterName = "Other" });
        world.Apply(HeavyShipsUpdate.FromShips(101, SimVec.Zero,
            [new Ship(2, Hull, new SimVec(200, 0, 0), SimVec.Zero, Orientation.Identity)]));
        return world;
    }
    private static BattleShipScene Scene() => new(new Dictionary<short, DataHullTypeIGC> { [1] = Hull });
    private static void Set(ShipEntity ship, string property, object? value) => typeof(ShipEntity).GetProperty(property)!.SetValue(ship, value);
    private static StagedAsset Asset(string source, byte[] bytes, params string[] dependencies)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new(source, "packs/" + hash, bytes.Length, hash, "application/octet-stream", "fixture original", dependencies);
    }
    private static readonly StagedAsset Core = Asset("core.igc", [1]);
    private static readonly StagedAsset Model = Asset("fig21.mdl", [2], "fig21bmp.mdl");
    private static readonly StagedAsset Texture = Asset("fig21bmp.mdl", [3]);
    private static readonly StagedManifest Resident = new(2, [Core]);
    private static BattleManifest Index() => new(1, [Model, Texture], [new("hull-1", [Model.Source, Texture.Source])]);

    [Fact]
    public async Task Contact_roster_survives_missing_mesh_then_swaps_only_after_complete_verified_pack()
    {
        var textureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new Cache(); int requests = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Interlocked.Increment(ref requests);
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(Texture.Sha256, StringComparison.Ordinal)) { textureStarted.TrySetResult(); await release.Task.WaitAsync(token); return Reply([3]); }
            return Reply(path.EndsWith(Core.Sha256, StringComparison.Ordinal) ? [1] : [2]);
        })) { BaseAddress = new("http://localhost/play/") };
        var destination = await new AssetStager(http, cache).StageAsync(Resident);
        var world = World(); var scene = Scene();
        await using var streamer = new BattlePackStreamer(Index(), Resident, new(http, cache), destination);
        scene.Gather(world, 1, streamer.RequestPack);
        Assert.Equal(2, scene.Ships.Count);
        Assert.All(scene.Ships, s => Assert.False(scene.MeshBytesReady(s.HullId, destination)));
        await textureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(destination.Contains(Model.Source)); // no partial closure
        release.TrySetResult();
        await Until(() => streamer.IsReady("hull-1"));
        scene.Gather(world, 1, streamer.RequestPack);
        Assert.Equal(2, scene.Ships.Count);
        Assert.All(scene.Ships, s => Assert.True(scene.MeshBytesReady(s.HullId, destination)));
        Assert.Equal(1, streamer.CompletedPacks); Assert.Equal(3, requests);
        Assert.Equal(1, cache.Commits); // streamed packs never evict the join manifest
        Assert.Equal(24, BattleShipScene.StandInSide(24) * MathF.Sqrt(3), 4);
        // Both renderers consume these same value-type contacts through the adapter.
        var contacts = scene.Ships.Select(s => new HudContact(HudContactKind.Ship, new(10, 10), true, 1, null, false, false, Name: s.CharacterName)).ToArray();
        Assert.Equal(2, HudModelAdapter.Create(null, CameraMode.Chase, contacts: contacts).Context.Contacts.Count);
    }

    [Theory]
    [InlineData("IsDead")]
    [InlineData("IsGone")]
    [InlineData("Unknown")]
    [InlineData("ParentId")]
    [InlineData("State")]
    [InlineData("StationId")]
    [InlineData("PoseSectorId")]
    [InlineData("SectorId")]
    [InlineData("Latest")]
    public void Visibility_gate_excludes_hidden_docked_parented_corpses_and_stale_cross_sector_poses(string field)
    {
        var world = World(); var scene = Scene();
        var other = world.Ships[2];
        object? value = field switch
        {
            "ParentId" => (short)1, "State" => ShipStatusMessage.StateDocked,
            "StationId" => (short)4, "PoseSectorId" or "SectorId" => (short)9,
            "Latest" => null, _ => true,
        };
        Set(other, field, value);
        var requests = new List<string>(); scene.Gather(world, 1, requests.Add);
        Assert.Equal(new short[] { 1 }, scene.Ships.Select(s => s.ShipId));
        Assert.Single(requests);
    }

    [Fact]
    public void Sector_index_tracks_wire_migration_removal_repeated_export_and_reset()
    {
        var world = World();
        Assert.Equal(2, world.ShipsInSector(1).Count);
        world.Apply(new ExportShip { ShipId = 2, HullId = 1, SideId = 1 });
        Assert.Equal(2, world.ShipsInSector(1).Count);
        world.Apply(new ShipStatusMessage { ShipId = 2, HullId = 1, SectorId = 9, StationId = -1, ParentId = -1, StateTime = 200 });
        Assert.Single(world.ShipsInSector(1)); Assert.Single(world.ShipsInSector(9));
        var scene = Scene(); scene.Gather(world, 9, null); Assert.Empty(scene.Ships); // strategic location is not a sighting
        world.Apply(new QuitMission { ShipId = 2 }); Assert.Empty(world.ShipsInSector(9));
        world.Reset(); Assert.Empty(world.ShipsInSector(1));
        world.Apply(new SetCluster { SectorId = 1, Ship = Pose(1) }); Assert.Single(world.ShipsInSector(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task On_demand_cache_hit_or_corrupt_hit_follows_size_hash_contract(bool corrupt)
    {
        var cache = new Cache(); cache.Files[Core.Sha256] = [1]; cache.Files[Model.Sha256] = corrupt ? [9] : [2]; cache.Files[Texture.Sha256] = [3];
        int requests = 0;
        using var http = new HttpClient(new Handler((_, _) => { requests++; return Task.FromResult(Reply([2])); })) { BaseAddress = new("http://localhost/") };
        var destination = await new AssetStager(http, cache).StageAsync(Resident);
        await using var streamer = new BattlePackStreamer(Index(), Resident, new(http, cache), destination);
        streamer.RequestPack("hull-1"); await Until(() => streamer.IsReady("hull-1"));
        Assert.Equal(corrupt ? 1 : 0, requests); Assert.True(BattleShipScene.HasMeshBytes(Hull, destination));
    }

    [Fact]
    public async Task Failure_keeps_standin_and_contacts_retry_swaps_and_disposal_stops_network()
    {
        var cache = new Cache(); cache.Files[Core.Sha256] = [1];
        int requests = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (Interlocked.Increment(ref requests) == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(Reply(request.RequestUri!.AbsolutePath.EndsWith(Model.Sha256, StringComparison.Ordinal) ? [2] : [3]));
        })) { BaseAddress = new("http://localhost/") };
        var destination = await new AssetStager(http, cache).StageAsync(Resident);
        var world = World(); var scene = Scene();
        var streamer = new BattlePackStreamer(Index(), Resident, new(http, cache), destination);
        scene.Gather(world, 1, streamer.RequestPack);
        await Until(() => streamer.RetryCount > 0);
        Assert.False(BattleShipScene.HasMeshBytes(Hull, destination)); Assert.Equal(2, scene.Ships.Count);
        await Until(() => streamer.IsReady("hull-1")); Assert.True(BattleShipScene.HasMeshBytes(Hull, destination));
        await streamer.DisposeAsync(); int before = requests; streamer.RequestPack("hull-1"); Assert.Equal(before, requests);
    }

    [Fact]
    public async Task Disposal_during_download_publishes_no_mesh()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new Cache(); cache.Files[Core.Sha256] = [1];
        using var http = new HttpClient(new Handler(async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Reply([2]); })) { BaseAddress = new("http://localhost/") };
        var destination = await new AssetStager(http, cache).StageAsync(Resident);
        var streamer = new BattlePackStreamer(Index(), Resident, new(http, cache), destination);
        streamer.RequestPack("hull-1"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await streamer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(BattleShipScene.HasMeshBytes(Hull, destination));
    }

    [Fact]
    public void Manifest_refuses_gameplay_missing_dependency_conflicting_hash_and_oversized_closure()
    {
        var index = Index(); index.Validate(Resident);
        Assert.Throws<InvalidDataException>(() => (index with { Assets = [Model with { Source = "hull.cvh" }, Texture] }).Validate(Resident));
        Assert.Throws<InvalidDataException>(() => (index with { Packs = [new("hull-1", [Model.Source])] }).Validate(Resident));
        Assert.Throws<InvalidDataException>(() => (index with { Assets = [Model, Texture with { Sha256 = Model.Sha256, Path = Model.Path, ByteSize = 2 }] }).Validate(Resident));
        Assert.Throws<InvalidDataException>(() => (index with { Assets = [Model with { ByteSize = StagedManifest.MaximumFileBytes + 1 }, Texture] }).Validate(Resident));
        // Missile and future effect renderers use exactly the same verified mechanism.
        (index with { Packs = [new("missile-1", [Model.Source, Texture.Source]), new("effect-fx18bmp", [Model.Source, Texture.Source])] }).Validate(Resident);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(5)] [InlineData(10)]
    public void Host_accepts_only_capped_battle_size(int bots) => Assert.Equal(bots, BattleOptions.Parse(["art", "assets", "scratch", "--bots", bots.ToString()]).Bots);
    [Theory]
    [InlineData("11")] [InlineData("-1")] [InlineData("ten")] [InlineData("2147483648")]
    public void Host_refuses_invalid_or_over_cap_bot_size(string bots) => Assert.Throws<ArgumentException>(() => BattleOptions.Parse(["art", "assets", "scratch", "--bots", bots]));
    [Fact]
    public void Host_default_has_no_bots_and_unknown_or_duplicate_flags_fail()
    {
        Assert.Equal(0, BattleOptions.Parse(["a", "b", "c"]).Bots);
        Assert.Throws<ArgumentException>(() => BattleOptions.Parse(["a", "b", "c", "--bots"]));
        Assert.Throws<ArgumentException>(() => BattleOptions.Parse(["a", "b", "c", "--bots", "1", "--bots", "2"]));
        Assert.Throws<ArgumentException>(() => BattleOptions.Parse(["a", "b", "c", "--anonymous", "1"]));
    }

    [Fact]
    public void Rock_culling_is_camera_relative_sphere_aware_and_conservative_at_far_plane()
    {
        var frustum = new BoundingFrustum(Matrix.CreatePerspectiveFieldOfView(MathHelper.PiOver4, 1, 10, 10000));
        Assert.True(BattleShipScene.RockInView(new(0, 0, -100), 10, frustum));
        Assert.False(BattleShipScene.RockInView(new(0, 0, 100), 10, frustum));
        Assert.False(BattleShipScene.RockInView(new(1000, 0, -100), 10, frustum));
        Assert.False(BattleShipScene.RockInView(new(0, 0, -12000), 100, frustum));
        Assert.True(BattleShipScene.RockInView(new(0, 0, -10020), 100, frustum));
    }

    [Fact]
    public void Culled_rocks_keep_spin_phase_without_per_rock_update_work()
    {
        var spins = new BrowserRockSpins();
        var rock = default(DataAsteroidIGC) with { AsteroidId = 7, ClusterId = 1, Rotation = [0, 1, 0, .2f] };
        spins.Register(rock);
        spins.Advance(1, .5); Assert.Equal(.1f, spins.Angle(7), 5);
        spins.Advance(9, 100); Assert.Equal(.1f, spins.Angle(7), 5); // another sector's clock
        for (int i = 0; i < 300; i++) spins.Advance(1, 1d / 60); // no angle/rotation queries while culled
        Assert.Equal(1.1f, spins.Angle(7), 5);
        spins.Register(rock); Assert.Equal(1.1f, spins.Angle(7), 5); // rediscovery does not restart phase
        var fresh = rock with { AsteroidId = 8 }; spins.Register(fresh); Assert.Equal(0, spins.Angle(8));
        spins.Advance(1, .25); Assert.Equal(.05f, spins.Angle(8), 5);
        spins.Clear(); spins.Register(rock); Assert.Equal(0, spins.Angle(7));
    }

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(5, timeout.Token);
    }
    private static HttpResponseMessage Reply(byte[] bytes)
    { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; response.Content.Headers.ContentType = new("application/octet-stream"); return response; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class Cache : IImmutableAssetCache
    {
        internal readonly Dictionary<string, byte[]> Files = new(); internal int Commits;
        public ValueTask<byte[]?> ReadAsync(string hash, CancellationToken token) => ValueTask.FromResult(Files.GetValueOrDefault(hash));
        public ValueTask RemoveAsync(string hash, CancellationToken token) { Files.Remove(hash); return ValueTask.CompletedTask; }
        public ValueTask<bool> PutAsync(StagedAsset asset, byte[] bytes, CancellationToken token) { Files[asset.Sha256] = bytes; return ValueTask.FromResult(true); }
        public ValueTask CommitManifestAsync(string identity, string[] hashes, CancellationToken token) { Commits++; return ValueTask.CompletedTask; }
    }
}
