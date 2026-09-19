using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Pure-domain tests (no sockets, no server) for the P263 explosion VFX:
/// the <see cref="ExplosionEvent"/>(207) wire codec, the world's transient
/// <see cref="ExplosionEntity"/> list fed from it, its expiry pruner, and the
/// pure <see cref="ExplosionPresets"/>/<see cref="ExplosionSampler"/> timing
/// rules. Messages are built directly and fed through
/// <see cref="ClientWorld.Apply"/>, exactly like the other ClientWorld*Tests.
/// <see cref="ClientWorld.Apply"/> stamps the local monotonic receipt instant,
/// so prune tests capture it and assert relative to that instant.
/// </summary>
public class ClientWorldExplosionTests
{
    // ── wire codec ──────────────────────────────────────────────────────────

    [Fact]
    public void ExplosionEvent_EncodesAndDecodes_RoundTripsAllFields()
    {
        var msg = new ExplosionEvent
        {
            Type = ExplosionType.BigShip,
            ClusterId = 1052,
            Scale = 960f,
            Position = new Vector3(1f, 2f, 3f),
            Velocity = new Vector3(-4f, 5f, -6f),
        };

        var decoded = ExplosionEvent.Decode(msg.Encode());

        Assert.Equal(ExplosionEvent.Fmid, decoded.GetFmid());
        Assert.Equal((ushort)207, ExplosionEvent.Fmid);
        Assert.Equal(ExplosionType.BigShip, decoded.Type);
        Assert.Equal((short)1052, decoded.ClusterId);
        Assert.Equal(960f, decoded.Scale);
        Assert.Equal(1f, decoded.Position.X);
        Assert.Equal(2f, decoded.Position.Y);
        Assert.Equal(3f, decoded.Position.Z);
        Assert.Equal(-4f, decoded.Velocity.X);
        Assert.Equal(5f, decoded.Velocity.Y);
        Assert.Equal(-6f, decoded.Velocity.Z);
    }

    [Fact]
    public void ExplosionEvent_IsFixedFortyBytes_HeaderMatches()
    {
        var msg = new ExplosionEvent { Type = ExplosionType.Mine };
        byte[] bytes = msg.Encode();
        Assert.Equal(ExplosionEvent.FixedSize, bytes.Length);
        Assert.Equal(40, bytes.Length);
        Assert.Equal((ushort)40, FedMessageHeader.Read(bytes).CbMsg);
        Assert.Equal((ushort)207, FedMessageHeader.Read(bytes).Fmid);
    }

    [Fact]
    public void ExplosionEvent_Decode_RejectsWrongFmid()
    {
        var msg = new MineDestroyed { ClusterId = 1, MineId = 2 };   // fmid 84
        Assert.Throws<ArgumentException>(() => ExplosionEvent.Decode(msg.Encode()));
    }

    // ── world ingestion ─────────────────────────────────────────────────────

    [Fact]
    public void ExplosionEvent_AppendsEntity_WithCapturedFields()
    {
        var world = new ClientWorld();
        world.Apply(Blast(ExplosionType.SmallShip, cluster: 7,
            pos: new Vector3(100f, 200f, 300f), vel: new Vector3(1f, 0f, 0f)));

        var e = Assert.Single(world.Explosions);
        Assert.Equal(ExplosionType.SmallShip, e.Type);
        Assert.Equal((short)7, e.ClusterId);
        Assert.Equal(100f, e.Position.X);
        Assert.Equal(200f, e.Position.Y);
        Assert.Equal(300f, e.Position.Z);
        Assert.Equal(1f, e.Velocity.X);
        Assert.NotEqual(0, e.ReceivedLocalMs);   // stamped on receipt
    }

    [Fact]
    public void Explosions_AppendInArrivalOrder_NotKeyedById()
    {
        var world = new ClientWorld();
        world.Apply(Blast(ExplosionType.Mine, cluster: 1));
        world.Apply(Blast(ExplosionType.LargeStation, cluster: 2));

        Assert.Equal(2, world.Explosions.Count);
        Assert.Equal(ExplosionType.Mine, world.Explosions[0].Type);
        Assert.Equal(ExplosionType.LargeStation, world.Explosions[1].Type);
    }

    [Fact]
    public void UnknownCategory_IsIgnored_NotThrown()
    {
        var world = new ClientWorld();
        world.Apply(new ExplosionEvent { Type = 999, Position = Vector3.Zero });

        Assert.Empty(world.Explosions);          // defensive: no explosion for a bad type
    }

    [Fact]
    public void PruneExpiredExplosions_RemovesFinishedBlast_KeepsFreshOne()
    {
        var world = new ClientWorld();
        world.Apply(Blast(ExplosionType.Mine, cluster: 1));          // 1.55 s
        world.Apply(Blast(ExplosionType.BigShip, cluster: 1));       // 5.35 s
        long now = world.Explosions[0].ReceivedLocalMs;

        // 2 s in: the mine (1.55 s) is done, the big ship (5.35 s) is not.
        world.PruneExpiredExplosions(now + 2000);

        var survivor = Assert.Single(world.Explosions);
        Assert.Equal(ExplosionType.BigShip, survivor.Type);

        world.PruneExpiredExplosions(now + 6000);   // past the big ship too
        Assert.Empty(world.Explosions);
    }

    [Fact]
    public void PruneExpiredExplosions_LeavesActiveBlast_AtBoundary()
    {
        var world = new ClientWorld();
        world.Apply(Blast(ExplosionType.BigShip, cluster: 1));       // 5.35 s
        long now = world.Explosions[0].ReceivedLocalMs;

        world.PruneExpiredExplosions(now + 5000);   // just under duration → still alive
        Assert.Single(world.Explosions);
    }

    // ── native preset table ────────────────────────────────────────────────

    [Theory]
    [InlineData(ExplosionType.Mine, 25f, 50f, 4)]
    [InlineData(ExplosionType.Missile, 1f, 0f, 6)]
    [InlineData(ExplosionType.SmallShip, 35f, 70f, 12)]
    [InlineData(ExplosionType.BigShip, 165f, 250f, 16)]
    [InlineData(ExplosionType.SmallStation, 100f, 800f, 16)]
    [InlineData(ExplosionType.Asteroid, 150f, 0f, 5)]
    [InlineData(ExplosionType.LargeStation, 350f, 1000f, 24)]
    public void Presets_MatchNativeAddExplosionTable(int type, float radiusExplosion, float radiusWave, int count)
    {
        var p = ExplosionPresets.For(type);
        Assert.Equal(count, p.CountDecals);
        Assert.Equal(radiusExplosion, p.RadiusExplosion, 3);
        Assert.Equal(radiusWave, p.RadiusShockWave, 3);
        Assert.Equal(radiusWave > 0f, p.HasShockWave);
    }

    [Fact]
    public void Presets_TintColours_MatchNativeSwitch()
    {
        Assert.Equal((154f / 255f, 130f / 255f, 190f / 255f), (ExplosionPresets.For(ExplosionType.Mine).ShockR, ExplosionPresets.For(ExplosionType.Mine).ShockG, ExplosionPresets.For(ExplosionType.Mine).ShockB));
        Assert.Equal((200f / 255f, 130f / 255f, 050f / 255f), (ExplosionPresets.For(ExplosionType.SmallShip).ShockR, ExplosionPresets.For(ExplosionType.SmallShip).ShockG, ExplosionPresets.For(ExplosionType.SmallShip).ShockB));
        Assert.Equal((220f / 255f, 130f / 255f, 050f / 255f), (ExplosionPresets.For(ExplosionType.SmallStation).ShockR, ExplosionPresets.For(ExplosionType.SmallStation).ShockG, ExplosionPresets.For(ExplosionType.SmallStation).ShockB));
    }

    [Fact]
    public void Presets_AssignTheCorrectSpriteFilms_PerCategory()
    {
        Assert.Equal(["exp24"], Stems(ExplosionType.Mine));
        Assert.Equal(["exp20"], Stems(ExplosionType.Missile));
        Assert.Equal(["exp25", "exp22"], Stems(ExplosionType.SmallShip));
        Assert.Equal(["exp25", "exp22"], Stems(ExplosionType.BigShip));
        Assert.Equal(["exp25", "exp23"], Stems(ExplosionType.SmallStation));
        Assert.Equal(["exp24"], Stems(ExplosionType.Asteroid));
        Assert.Equal(["exp25", "exp23"], Stems(ExplosionType.LargeStation));
    }

    [Fact]
    public void Durations_MatchNativeAnimationTiming()
    {
        // (count-1)*0.25 stagger + the LAST sprite's film frames/20 at 20 fps
        // (film alternates, so the last sprite's film decides the tail). The
        // shock wave runs in parallel ≤ 1s, never the longest.
        Assert.Equal(1.55f, ExplosionPresets.For(ExplosionType.Mine).TotalDurationSeconds, 3);          // 0.75 + 16/20
        Assert.Equal(2.25f, ExplosionPresets.For(ExplosionType.Missile).TotalDurationSeconds, 3);       // 1.25 + 20/20
        Assert.Equal(4.25f, ExplosionPresets.For(ExplosionType.SmallShip).TotalDurationSeconds, 3);     // 2.75 + 30/20 (last sprite film = exp22)
        Assert.Equal(5.25f, ExplosionPresets.For(ExplosionType.BigShip).TotalDurationSeconds, 3);       // 3.75 + 30/20 (last sprite film = exp22)
        Assert.Equal(5.65f, ExplosionPresets.For(ExplosionType.SmallStation).TotalDurationSeconds, 3);  // 3.75 + 38/20 (last sprite film = exp23)
        Assert.Equal(1.80f, ExplosionPresets.For(ExplosionType.Asteroid).TotalDurationSeconds, 3);      // 1.00 + 16/20
        Assert.Equal(7.65f, ExplosionPresets.For(ExplosionType.LargeStation).TotalDurationSeconds, 3);  // 5.75 + 38/20
    }

    // ── sampler timing (native ExplosionGeo) ───────────────────────────────

    [Fact]
    public void Sample_AtZero_OnlyFirstSpriteBegin_WithShockWaveAtFullBright()
    {
        var s = ExplosionSampler.Sample(ExplosionPresets.For(ExplosionType.SmallShip), 0f);

        // Only sprite 0 has begun (others stagger 0.25s later).
        var fb = Assert.Single(s.Fireballs);
        Assert.Equal(0, fb.SpriteIndex);
        Assert.Equal(0, fb.FrameIndex);
        Assert.Equal(0, fb.FilmIndex);            // film[0] = exp25

        Assert.NotNull(s.Shockwave);
        Assert.Equal(1f, s.Shockwave!.Value.Bright, 3);   // bright = 1 - 0²
        Assert.Equal(0f, s.Shockwave!.Value.Scale, 3);    // scale = t * radiusWave = 0
    }

    [Fact]
    public void Sample_AdvancesFrames_AtTwentyFps()
    {
        var preset = ExplosionPresets.For(ExplosionType.SmallShip);
        var s = ExplosionSampler.Sample(preset, 0.055f);   // sprite0 local = 0.055

        var fb = Assert.Single(s.Fireballs);
        Assert.Equal(0, fb.SpriteIndex);
        Assert.Equal(1, fb.FrameIndex);                    // floor(0.055 * 20) = 1
    }

    [Fact]
    public void Sample_Stagger_BringsInLaterSpritesOverTime()
    {
        var preset = ExplosionPresets.For(ExplosionType.SmallShip);
        // t=0.26: sprite 0 (start 0) + sprite 1 (start 0.25) both live.
        var s = ExplosionSampler.Sample(preset, 0.26f);

        Assert.Equal(2, s.Fireballs.Count);
        Assert.Contains(s.Fireballs, f => f.SpriteIndex == 0);
        Assert.Contains(s.Fireballs, f => f.SpriteIndex == 1);
        // Film alternates per sprite: 0→exp25(0), 1→exp22(1).
        Assert.Equal(0, s.Fireballs.First(f => f.SpriteIndex == 0).FilmIndex);
        Assert.Equal(1, s.Fireballs.First(f => f.SpriteIndex == 1).FilmIndex);
    }

    [Fact]
    public void Sample_SpritesFinish_WhenTheirFilmFramesExhaust()
    {
        var preset = ExplosionPresets.For(ExplosionType.Mine);   // single exp24 (16 frames), count 4
        // Last sprite starts at 3*0.25 = 0.75, lives 16/20 = 0.8 → gone at 1.55.
        var s = ExplosionSampler.Sample(preset, 1.54f);
        Assert.NotEmpty(s.Fireballs);                            // still just ending
        var done = ExplosionSampler.Sample(preset, 1.56f);
        Assert.Empty(done.Fireballs);                            // all finished
    }

    [Fact]
    public void Sample_ShockWave_FadesQuadratically_ExpandsLinearly()
    {
        var preset = ExplosionPresets.For(ExplosionType.BigShip);   // radiusWave 250
        var s = ExplosionSampler.Sample(preset, 0.5f);
        Assert.NotNull(s.Shockwave);
        Assert.Equal(0.75f, s.Shockwave!.Value.Bright, 3);          // 1 - 0.25
        Assert.Equal(125f, s.Shockwave!.Value.Scale, 3);            // 0.5 * 250

        var gone = ExplosionSampler.Sample(preset, 1.005f);         // bright ≤ 0
        Assert.Null(gone.Shockwave);
    }

    [Fact]
    public void Sample_NoShockWaveCategory_HasNone()
    {
        var s = ExplosionSampler.Sample(ExplosionPresets.For(ExplosionType.Missile), 0f);
        Assert.Null(s.Shockwave);
    }

    [Fact]
    public void Sample_FireballOffsetAndAngle_AreDeterministic()
    {
        var preset = ExplosionPresets.For(ExplosionType.SmallShip);
        var a = ExplosionSampler.Sample(preset, 0.1f);
        var b = ExplosionSampler.Sample(preset, 0.1f);
        var fa = a.Fireballs.Single();
        var fb = b.Fireballs.Single();
        Assert.Equal(fa.Offset.X, fb.Offset.X);
        Assert.Equal(fa.Offset.Y, fb.Offset.Y);
        Assert.Equal(fa.Angle, fb.Angle);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static ExplosionEvent Blast(int type, short cluster = 1, Vector3? pos = null, Vector3? vel = null) => new()
    {
        Type = type,
        ClusterId = cluster,
        Scale = 1f,
        Position = pos ?? Vector3.Zero,
        Velocity = vel ?? Vector3.Zero,
    };

    private static string[] Stems(int type) =>
        ExplosionPresets.For(type).Films.Select(f => f.Stem).ToArray();
}
