using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P1005 — where the asteroid spin <b>comes from</b>, pinned to native:
/// <list type="bullet">
/// <item>every generated rock's wire <c>Rotation</c> is
/// <c>axis = forward</c> + <c>angle = 0.2 / signature</c> with
/// <c>signature = radius / 100</c> — native <c>CreateAsteroid</c>
/// (<c>Igc/common.cpp:2866-2871</c>), the same rad/s rate
/// <c>CmodelIGC::Update</c> consumes (<c>Igc/modelIGC.cpp:85-99</c>);</item>
/// <item>the sim's own model spin (P766, <c>CasteroidIGC::Update</c> /
/// <c>modelIGC.cpp:85-99</c>) never leaks into the wire pose, so a client that
/// also spins a rock locally can never double-rotate it (server pose + client
/// spin): the exported <c>DataAsteroidIGC</c> blob is byte-identical after the
/// sim has turned the runtime orientation.</item>
/// </list>
/// </summary>
public sealed class P1005AsteroidSpinSourceTests
{
    private static readonly Lazy<StaticCore> Core = new(MapGenTestCore.LoadRepresentative);
    private static readonly string ArtRoot = TestArtRoot.Value;

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(MapGenType.SingleRing)]
    [InlineData(MapGenType.SnowFlake)]
    [InlineData(MapGenType.HiLo)]
    [InlineData(MapGenType.Grid)]
    [InlineData(MapGenType.Brawl)]
    public void Generated_rock_rotation_is_native_axis_forward_and_angle_twenty_over_radius(MapGenType type)
    {
        Require.Artwork();
        var map = MissionMapGenerator.Generate(
            new MapGenParams { Type = type, Teams = 2, CivIds = [1, 3] }, Core.Value, 0);
        Assert.NotEmpty(map.Asteroids);

        foreach (var rock in map.Asteroids)
        {
            // common.cpp:2866-2871 — signature = radius/100 and angle = 0.2/signature
            // (radians per second: the value CmodelIGC::Update multiplies by dt).
            float signature = rock.Radius / 100.0f;
            Assert.Equal(0.2f / signature, rock.Rotation[3], 4);

            // da.rotation.axis(da.forward) — the rock's own forward is the spin axis.
            Assert.Equal(rock.Forward[0], rock.Rotation[0], 3);
            Assert.Equal(rock.Forward[1], rock.Rotation[1], 3);
            Assert.Equal(rock.Forward[2], rock.Rotation[2], 3);

            // Native band: radius 100..800 -> 0.025..0.2 rad/s (<= 11.5 deg/s).
            Assert.InRange(rock.Rotation[3], 0.02f, 0.21f);
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Sim_model_spin_never_reaches_the_exported_wire_pose()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "m2xspiderw.igc")));
        var mission = Mission.Assemble(
            core, map, [new SideSpec(0, "Alpha", 1)], new RecordingIgcSite(), 0);
        var sector = mission.Sectors.First(s => s.Mineable.Count > 0);
        var rock = sector.Mineable[0];

        // The blob the server exports (join burst / discovery / ASTEROIDS_UPDATE ore
        // path all send DataAsteroidIGC.FromAsteroid(rock.Data)) before any ticks.
        byte[] exportedBefore = ExportAsteroid.FromAsteroid(rock.Data).Encode();
        var recordBefore = (float[])rock.Data.Forward.Clone();

        const int ticks = 100;
        const float dt = 0.05f;                       // 5 s of sim at the 20 Hz cadence
        for (int i = 1; i <= ticks; i++) sector.Update(i * dt);

        // The sim model did turn — native's per-tick angle x dt (modelIGC.cpp:85-99).
        // The rock's record carries zero forward/up, so its seeded orientation is
        // identity and the turned angle is read straight off the matrix trace.
        static float TurnedAngle(Orientation o)
        {
            float trace = o.GetRight().X + o.GetUp().Y + o.GetBackward().Z;
            return MathF.Acos(Math.Clamp((trace - 1f) / 2f, -1f, 1f));
        }
        Assert.Equal(0f, TurnedAngle(new Orientation()), 4);          // identity base
        float turned = TurnedAngle(rock.Orientation);
        Assert.Equal(rock.Data.Rotation[3] * ticks * dt, turned, 2);  // rate x elapsed
        Assert.True(turned > 0.5f, $"the sim rock must have turned: {turned:F3} rad");

        // ... but the wire record the client draws from is unchanged, so client spin
        // + server pose can never double-apply.
        Assert.Equal(exportedBefore, ExportAsteroid.FromAsteroid(rock.Data).Encode());
        Assert.Equal(recordBefore.Length, rock.Data.Forward.Length);
        for (int i = 0; i < recordBefore.Length; i++)
            Assert.Equal(recordBefore[i], rock.Data.Forward[i]);
    }
}
