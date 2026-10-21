using System.Buffers.Binary;
using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P773 — the own Kill Bonus WITHOUT fmid 212: LIVE proof that a real kill
/// streak reaches a real <see cref="ConnectionSession"/> client purely through
/// the HUD-20 experience path (PLAYERINFO.fExperience seed, appmsg.cpp:2280;
/// KILL_SHIP +1 on a cross-side credit, appmsg.cpp:2807-2811), and that the
/// per-tick fmid-212 kill-bonus stream is gone — no captured
/// frame carries 212 and the client never records it as unknown. The percent
/// the client derives (<c>50·c/(c+4)</c>, console.cpp:824-828) is the same
/// value the HUD gauge shows. [Collection("LiveNetwork")] keeps this
/// sequential against the other live suites.
/// </summary>
[Collection("LiveNetwork")]
public class KillBonusWireLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon)> Fight = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var hull in core.HullTypes.Values
                     .Where(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod))
                     .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                     .OrderBy(h => h.HullId))
        {
            foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                         .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                         .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                         .OrderBy(w => w.EnergyPerShot))
            {
                if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Power > 0f)
                    return (hull, p);
            }
        }
        throw new InvalidOperationException("no fightable non-lifepod hull+weapon in the representative core");
    });

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // this ConnectionSession peer never sends POSITIONREQ
        Games =
        [
            new Gm { Name = "Test Mission", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest SampleRequest(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 926u, DwCookie = 0x926u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 9300000000000000000ul, DrmHash = "pscore773",
    };

    private const float TargetZ = -180f;

    private static (Srv server, Ship shooterShip, ConnectionSession shooter) SeatShooter()
    {
        var server = new Srv { SpawnInitialMinersAtBoot = false };
        var placedShooter = false;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (hull, weapon) = Fight.Value;
            Vector3 pos = !placedShooter ? new Vector3(0, 0, 0) : new Vector3(0, 0, TargetZ);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, mission.Sectors[0]);
            ship.SideId = (short)(placedShooter ? 1 : 0);
            if (!placedShooter) { ship.Energy = hull.MaxEnergy * 100f; ship.MountWeapon(weapon); }
            placedShooter = true;
            return (ship, mission.Sectors[0]);
        };
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        var session = new ConnectionSession();
        session.Connect(server.BoundPort, "SHOOTER");
        session.SendLogonRequest(SampleRequest("SHOOTER"));
        bool Seated() { lock (session.Gate) return session.World.MyShipId is not null; }
        int ticks = 0;
        while (!Seated() && ticks++ < 8000) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(Seated(), "shooter client must seat");

        lock (session.Gate)
        {
            short shooterId = session.World.MyShipId!.Value;
            var shooterShip = server.Ships.First(s => s.Ship.Id == shooterId).Ship;
            return (server, shooterShip, session);
        }
    }

    private static readonly MethodInfo? BroadcastKillShip = typeof(Srv).GetMethod(
        "BroadcastKillShip", BindingFlags.Instance | BindingFlags.NonPublic);

    private static void FireKill(Srv server, Ship victim, Ship? launcher, float amount) =>
        BroadcastKillShip!.Invoke(server, new object?[]
        {
            1000.0, victim, launcher, amount, new Vector3(0, 0, 0), new Vector3(0, 0, 0),
        });

    /// <summary>⭐ A real kill streak reaches the client's derived kill bonus
    /// WITHOUT fmid 212: after each cross-side kill the real ConnectionSession's
    /// <see cref="Allegiance.ClientCore.State.ClientWorld.KillBonusPercent"/>
    /// reaches exactly <c>50·c/(c+4)</c> (the client-side experience path), and
    /// at the end no captured server frame carried fmid 212 nor did the client
    /// record 212 as an unknown fmid. The percent assertions hold on both the
    /// old (streamed) and new (derived) implementations; the 212 assertions are
    /// the ones that fail on the unfixed code.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_kill_streak_reaches_the_client_kill_bonus_without_fmid_212()
    {
        Require.Artwork();
        var seat = SeatShooter();
        using (var server = seat.server)
        using (var shooter = seat.shooter)
        {
            var shooterShip = seat.shooterShip;
            var mission = shooterShip.Mission!;
            var sector = shooterShip.Sector!;
            var (hull, _) = Fight.Value;

            // Post-death baseline (native reset) — the derived readout starts at 0.
            shooterShip.ResetKillBonus();

            // Capture EVERY outbound server frame (both send lanes) and record
            // the fmids it carried, so the tail of the test can prove 212 never
            // crossed the wire.
            var capturedFmids = new List<ushort>();
            object captureGate = new();
            server._testSendCapture = (_, packet) =>
            {
                foreach (var msg in FedPacket.Unpack(packet))
                    lock (captureGate) capturedFmids.Add(BinaryPrimitives.ReadUInt16LittleEndian(msg.AsSpan(2)));
            };
            server._testSendCoalescingCapture = (_, packet, _, _) =>
            {
                foreach (var msg in FedPacket.Unpack(packet))
                    lock (captureGate) capturedFmids.Add(BinaryPrimitives.ReadUInt16LittleEndian(msg.AsSpan(2)));
            };

            // Pump enough to receive the PLAYERINFO seed (experience 0 → 0%).
            int primed = PumpUntil(server, () =>
            {
                lock (shooter.Gate) return shooter.World.MyShipId is { } me
                    && shooter.World.Ships.TryGetValue(me, out var own) && own.Player is not null;
            }, 400);
            Assert.True(primed < 400, "client should receive its own PLAYERINFO seed");
            lock (shooter.Gate) Assert.Equal(0f, shooter.World.KillBonusPercent, 3);
            Console.WriteLine($"P773-LIVE: client baseline KB%={shooter.World.KillBonusPercent:R}");

            for (int i = 0; i < 4; i++)
            {
                var npc = new Ship((short)(955 + i), hull, new Vector3(0, 0, TargetZ), Vector3.Zero, Orientation.Identity)
                { SideId = 1 };                       // cross-side, non-lifepod fight hull
                mission.PlaceShip(npc, sector);

                // The client's cross-side check (appmsg.cpp:2807-2811) needs the
                // victim's id AND side — wait until the client knows both.
                int known = PumpUntil(server, () =>
                {
                    lock (shooter.Gate) return shooter.World.Ships.TryGetValue(npc.Id, out var e) && e.SideId == 1;
                }, 400);
                Assert.True(known < 400, $"client must know victim {npc.Id} and its side before kill {i + 1}");

                FireKill(server, npc, shooterShip, amount: -1f);

                float expected = 50f * (i + 1) / (i + 5);   // counter (i+1): 50·c/(c+4)
                bool Reached() { lock (shooter.Gate) return MathF.Abs(shooter.World.KillBonusPercent - expected) < 5e-4f; }
                int waited = PumpUntil(server, Reached, 400);
                Assert.True(waited < 400, $"client kill bonus should reach {expected:R} after kill {i + 1}");
                lock (shooter.Gate)
                    Console.WriteLine($"P773-LIVE: after kill #{i + 1} client KB%={shooter.World.KillBonusPercent:R} (expect {expected:R})");
                Assert.True(Reached(), "re-check under the gate");
                Assert.True(shooter.World.KillBonusPercent <= 50f, "never exceeds the cap");
            }

            // No captured frame carried the retired fmid 212 — the per-tick
            // stream is gone, and the client never saw it as an unknown fmid.
            ushort[] fmids;
            lock (captureGate) fmids = capturedFmids.ToArray();
            Assert.DoesNotContain((ushort)212, fmids);
            Assert.DoesNotContain((ushort)212, shooter.UnknownFmids);
            Console.WriteLine($"P773-LIVE: {fmids.Length} captured frames, none carried fmid 212");
        }
    }
}
