using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P298 — Kill Bonus WIRE: LIVE proof that a REAL kill streak travels from the
/// real server's per-tick owner-only <see cref="OwnShipKillBonusUpdate"/>(212)
/// stream into the renderer-only client's feed (here a connected
/// <see cref="FltBot"/> standing in for the MonoGame client that feeds the HUD
/// gauge). Reuses P278's <see cref="KillBonusLiveTests"/> harness: real
/// in-process <see cref="Srv"/>, real bots over loopback TCP, real
/// <c>BroadcastKillShip</c> kill credit. The gauge reads
/// <c>ClientWorld.KillBonusPercent</c>, which this stream is the sole feed for
/// — so the last received update per kill proving the exact <c>50·c/(c+4)</c>
/// value IS the "gauge tracks the real live value" proof at the data layer.
/// [Collection("LiveNetwork")] keeps this sequential against the other live
/// suites (same as P215/P278).
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
        SteamID = 9300000000000000000ul, DrmHash = "pscore298",
    };

    private const float TargetZ = -180f;

    private static (Srv server, Ship shooterShip, FltBot shooter, FltBot target) SeatTwoPeers()
    {
        var server = new Srv();
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

        var a = new FltBot();
        var b = new FltBot();
        a.Connect(server.BoundPort, "SHOOTER"); b.Connect(server.BoundPort, "TARGET");
        a.SendLogonRequest(SampleRequest("SHOOTER")); b.SendLogonRequest(SampleRequest("TARGET"));
        int got = PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "both bots must seat");

        var aSet = a.ShipAssignment!; var bSet = b.ShipAssignment!;
        bool aAtOrigin = aSet.Ship.Position.Z > -1f;
        var shooter = aAtOrigin ? a : b;
        var target = aAtOrigin ? b : a;
        short shooterId = (aAtOrigin ? aSet : bSet).Ship.ShipId;
        var shooterShip = server.Ships.First(s => s.Ship.Id == shooterId).Ship;
        return (server, shooterShip, shooter, target);
    }

    private static readonly MethodInfo? BroadcastKillShip = typeof(Srv).GetMethod(
        "BroadcastKillShip", BindingFlags.Instance | BindingFlags.NonPublic);

    private static void FireKill(Srv server, Ship victim, Ship? launcher, float amount) =>
        BroadcastKillShip!.Invoke(server, new object?[]
        {
            1000.0, victim, launcher, amount, new Vector3(0, 0, 0), new Vector3(0, 0, 0),
        });

    /// <summary>The most recent <see cref="OwnShipKillBonusUpdate"/> the shooter
    /// bot has received, or null if none yet.</summary>
    private static OwnShipKillBonusUpdate? LatestKB(FltBot shooter)
    {
        var all = shooter.OwnShipKillBonusUpdates;
        return all.Count == 0 ? null : all[^1];
    }

    /// <summary>⭐ A real kill streak climbs the value the CLIENT receives: after
    /// each real kill the shooter bot's most recent <see cref="OwnShipKillBonusUpdate"/>
    /// carries exactly <c>50·c/(c+4)</c> (and the streak count) — the real live
    /// value that drives the HUD Kill Bonus gauge. Also proves a fresh ship
    /// streams 0% (an honest readout, never NA).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_kill_streak_streams_climbing_kill_bonus_to_the_client()
    {
        Require.Artwork();
        var seat = SeatTwoPeers();
        using (var server = seat.server)
        using (var shooter = seat.shooter)
        using (var target = seat.target)
        {
            var shooterShip = seat.shooterShip;
            var mission = shooterShip.Mission!;
            var sector = shooterShip.Sector!;
            var (hull, _) = Fight.Value;

            // Post-death baseline (native reset) — the gauge should show 0 first.
            shooterShip.ResetKillBonus();
            shooter.ResetOwnShipKillBonus();

            // Pump enough to receive at least one streamed 0% baseline.
            int primed = PumpUntil(server, () => LatestKB(shooter) is { } kb && kb.Streak == 0, 200);
            Assert.True(primed < 200, "client should first receive the 0% baseline");
            Assert.Equal(0f, LatestKB(shooter)!.KillBonusPercent, 3);
            Console.WriteLine($"P298-LIVE: client stream baseline KB%={LatestKB(shooter)!.KillBonusPercent:R} streak={LatestKB(shooter)!.Streak}");

            for (int i = 0; i < 4; i++)
            {
                var npc = new Ship((short)(955 + i), hull, new Vector3(0, 0, TargetZ), Vector3.Zero, Orientation.Identity)
                { SideId = 1 };                       // cross-side, non-lifepod fight hull
                mission.PlaceShip(npc, sector);

                FireKill(server, npc, shooterShip, amount: -1f);

                float expected = 50f * (i + 1) / (i + 5);   // counter (i+1): 50·c/(c+4)
                int waited = PumpUntil(server,
                    () => LatestKB(shooter) is { } kb && kb.Streak == i + 1,
                    200);
                Assert.True(waited < 200, $"client should receive streak {i + 1}");
                var received = LatestKB(shooter)!;
                Console.WriteLine($"P298-LIVE: after kill #{i + 1} client stream KB%={received.KillBonusPercent:R} " +
                                  $"streak={received.Streak} (expect {expected:R})");
                Assert.Equal((short)(i + 1), received.Streak);
                Assert.Equal(expected, received.KillBonusPercent, 3);
                Assert.True(received.KillBonusPercent <= 50f, "never exceeds the cap");
            }
        }
    }
}
