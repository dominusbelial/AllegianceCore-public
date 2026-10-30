using System.Reflection;
using System.Diagnostics;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P890OwnAimLoopbackTests(ITestOutputHelper output)
{
    private static FieldInfo Field(Type type, string name) => type.GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static float Angle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(
        (a.Normalize() * b.Normalize()), -1, 1)) * 180 / MathF.PI;

    private void Distribution(string label, List<float> samples)
    {
        samples.Sort();
        output.WriteLine($"P890 {label}: n={samples.Count} median={samples[samples.Count / 2]:F4} " +
            $"p95={samples[(int)((samples.Count - 1) * .95)]:F4} max={samples[^1]:F4}");
    }

    // Production TCP session + own simulator, outside Game1.TrainingProof. Pin
    // port explicitly only to compare the retired broadcast policy with baseline.
    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData(true, "native")]
    [InlineData(true, "port")]
    [InlineData(false, "native")]
    [InlineData(false, "port")]
    public void Turning_and_firing_preserve_client_aim(bool training, string model)
    {
        Require.Artwork();
        using var host = training ? TrainingHost.Start(TrainingHost.BasicWeaponry) : null;
        using var server = host is null ? new Srv() : (Srv)Field(typeof(TrainingHost), "_server").GetValue(host)!;
        Ship? spawned = null;
        if (host is null)
        {
            Field(typeof(Srv), "ShipSpawner").SetValue(server,
                (StaticCore core, Mission mission, short id) =>
                {
                    var hull = core.HullTypes.Values.First(h => h.MaxFixedWeapons > 0 && h.Thrust > 0);
                    var ship = new Ship(id, hull, new Vector3(500, 620, 830), Vector3.Zero, Orientation.Identity) { SideId = 0 };
                    mission.PlaceShip(ship, mission.Sectors[0]);
                    spawned = ship;
                    return (ship, mission.Sectors[0]);
                });
            server.Boot(new()
            {
                Port = 0, Discovery = new() { Enabled = false },
                Broadcast = new() { ShipUpdateModel = model, ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = true },
                Transport = new() { Mode = "pool" }, Sim = new() { NativeGotoPlan = true },
                Games = [new() { Name = "P890", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
            });
        }
        // Training has its real authored map, loadout and origin. It normally uses
        // native; select the comparison model before connecting, without a ticker.
        Field(typeof(Srv), "_shipUpdateModel").SetValue(server, model);
        Field(typeof(Srv), "_nativeShipUpdates").SetValue(server, model == "native");
        foreach (var mission in server.Missions) mission.Stage = MissionStage.Started;
        using var peer = new ConnectionSession();
        peer.Connect(server.BoundPort, "P890");
        peer.SendLogonRequest(new()
        {
            CharacterName = "P890", FedsrvVer = LogonRequest.MsgVer, ZoneTicket = [1, 2, 3, 4],
            CDKey = "ABCDEFGHIJKL", CrcFileList = -1, DwCookie = uint.MaxValue, SteamAuthTicket = new byte[16],
        });
        void Pump(Func<bool> done)
        {
            for (int i = 0; i < 3000 && !done(); i++) { server.TickSim(); Thread.Sleep(1); }
            Assert.True(done(), "loopback handshake timed out");
        }
        Pump(() => peer.Ack is not null);
        peer.SendPositionReq(0);
        Pump(() => { lock (peer.Gate) return peer.World.MyShipId is { } me && peer.World.Ships.TryGetValue(me, out var own) && own.Latest is not null; });
        var ship = host?.PlayerShip ?? spawned!;
        // Use a real core weapon with zero dispersion. Continuous ammunition and
        // energy remove resource exhaustion from this pose/shot-direction test.
        ship.ClearLoadout();
        var weapon = ship.Mission!.Core.PartTypes.Values
            .Where(p => p.Type is DataPartTypeIGC.Weapon)
            .Select(p => (DataPartTypeIGC.Weapon)p.Type)
            .First(w => w.Dispersion == 0 && w.DtimeBurst > 0 && w.DtimeBurst < .2f);
        ship.MountWeapon(weapon);
        var sim = new OwnShipSimulator();
        sim.SetHull(ShipDeadReckoner.HullStats.FromHull(ship.HullType,
            ship.GlobalAttributeFactor(GlobalAttributeSet.Thrust),
            ship.GlobalAttributeFactor(GlobalAttributeSet.MaxSpeed),
            ship.GlobalAttributeFactor(GlobalAttributeSet.TurnRate),
            ship.GlobalAttributeFactor(GlobalAttributeSet.TurnTorque)), 0);
        sim.Seed(ship.Position, ship.Velocity, ship.Orientation, ship.GetTurnRates(new float[3]));
        byte cookie;
        lock (peer.Gate) cookie = peer.World.Ships[peer.World.MyShipId!.Value].Cookie;
        var angles = new List<float>();
        var distances = new List<float>();
        var shots = new List<float>();
        var seen = new HashSet<short>();
        var transportHost = Field(typeof(Srv), "_host").GetValue(server)!;
        var packetsField = Field(transportHost.GetType(), "_packetsReceived");
        var costs = new List<float>();
        // 12 seconds of hard yaw/pitch reversals under sustained fire: hover, then cruise.
        // Every 0.15s snapshot describes the current instant; then both sims advance.
        // No Game1 proof steering, direct server pose writes, or reset suppression.
        for (int i = 0; i < 240; i++)
        {
            var controls = new ControlData(i / 29 % 2 == 0 ? 1 : -1, i / 61 % 2 == 0 ? .7f : -.7f, 0, i < 120 ? -1 : .2f);
            if (i % 3 == 0)
            {
                uint time = (uint)Math.Round(server.SimNow * 1000);
                long before = (long)packetsField.GetValue(transportHost)!;
                peer.SendShipPose(time, cookie, ClientShipUpdate.FromPose(time, sim.Position, sim.Velocity,
                    sim.Orientation, ShipControlState.AllWeapons, controls, sim.TurnRates, sim.Power));
                var deadline = Stopwatch.StartNew();
                while ((long)packetsField.GetValue(transportHost)! == before && deadline.ElapsedMilliseconds < 2000) Thread.Sleep(1);
                Assert.True((long)packetsField.GetValue(transportHost)! > before, "TCP pose never decoded");
            }
            sim.Step(.05f, controls, ShipControlState.AllWeapons);
            ship.Ammo = ship.HullType.MaxAmmo;
            ship.Energy = ship.HullType.MaxEnergy;
            long started = Stopwatch.GetTimestamp();
            server.TickSim();
            costs.Add((float)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            angles.Add(Angle(sim.Orientation.GetForward(), ship.Orientation.GetForward()));
            distances.Add((sim.Position - ship.Position).Length());
            foreach (var projectile in ship.Sector!.Models.OfType<Projectile>())
                if (ReferenceEquals(projectile.Launcher, ship) && seen.Add(projectile.Id))
                    shots.Add(Angle(sim.Orientation.GetForward(), projectile.Forward));
        }
        string label = $"{(training ? "training" : "skirmish")}/{model}";
        Distribution(label + "/forward-deg", angles);
        Distribution(label + "/position-m", distances);
        Distribution(label + "/tick-ms", costs);
        Assert.NotEmpty(shots);
        Distribution(label + "/shot-deg", shots);
        // 0.15s send cadence delays reversals by <=0.1s; at these hull rates
        // allow 3 degrees at the reversal transient (including compact rounding).
        // The p95 excludes the few reversal boundaries; steady aim must track.
        Assert.True(angles[(int)((angles.Count - 1) * .95)] < 2, label + " sustained aim drift");
        Assert.True(angles[^1] < 3, label + " peak aim drift");
        Assert.True(shots[^1] < 3, label + " projectile aim drift");
        Assert.True(distances[^1] < 5, label + " movement drift");
        lock (peer.Gate) Assert.False(peer.World.Ships[peer.World.MyShipId!.Value].HasPendingReset);
    }
}
