using Allegiance.Core;
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
/// P173: the live loopback proof that a ship-loadout mutation broadcasts a real
/// <c>FM_S_LOADOUT_CHANGE(96)</c> to the changed ship's own peer and its same-side
/// teammates who hold that ship's <c>ExportShip</c> — and that a cross-side enemy
/// receives none. Three things are asserted for real:
/// <list type="number">
/// <item>mounting a part broadcasts a LOADOUT_CHANGE carrying the changed ship's
/// correct hull id and the just-mounted part in its <c>Parts</c> tail — to the
/// subject's own peer and to a same-side teammate; removing that part broadcasts
/// a LOADOUT_CHANGE whose <c>Parts</c> no longer contain it;</item>
/// <item>a gunner boarding a ship broadcasts a LOADOUT_CHANGE on the <b>parent</b>
/// ship whose <c>Passengers</c> now includes the boarder's ship id + turret id;
/// the gunner leaving broadcasts one whose <c>Passengers</c> no longer includes
/// it;</item>
/// <item>the cross-side enemy receives none of these (this port's ship
/// visibility is same-side only, matching every prior per-ship broadcast).</item>
/// </list>
/// Structure mirrors <see cref="MineBroadcastTests"/> (3 bots / round-robin
/// sides, a guaranteed same-side pair plus a cross-side enemy). The mutation is
/// triggered by calling the public <c>Ship</c> mutation methods directly on the
/// server-side <c>Ship</c> — the same established live-test entry point
/// MineBroadcastTests uses for <c>Ship.DeployMine</c>.
/// </summary>
[Collection("LiveNetwork")]
public class LoadoutChangeLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Loadout Change", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 852u, DwCookie = 0x852u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8710000000000000000ul, DrmHash = "loadout",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>One bot pair whose ships are on the same side (the round-robin
    /// side assignment guarantees such a pair), plus the leftover cross-side
    /// enemy. Returns (subjectIdx, observerIdx, enemyIdx).</summary>
    private static (int Subject, int Observer, int Enemy) Roles(
        FltBot[] bots, Dictionary<short, short?> ships, short[] ids)
    {
        int subject = -1, observer = -1;
        for (int i = 0; i < bots.Length && observer < 0; i++)
            for (int j = 0; j < bots.Length; j++)
                if (j != i && ships[ids[j]] == ships[ids[i]]) { subject = i; observer = j; break; }
        Assert.True(subject >= 0, "round-robin must yield a same-side pair");
        int enemy = -1;
        for (int k = 0; k < bots.Length; k++)
            if (k != subject && k != observer) { enemy = k; break; }
        Assert.True(enemy >= 0 && ships[ids[enemy]] != ships[ids[subject]],
            "3 bots / 2 teams must leave one bot cross-side");
        return (subject, observer, enemy);
    }

    /// <summary>A real weapon part the subject will mount — any mounted part
    /// echoes, so the first nonzero-PartId weapon suffices.</summary>
    private static DataPartTypeIGC RealWeapon(Mission mission)
    {
        var w = mission.Core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon && p.PartId != 0);
        return w;
    }

    private static FltBot[] BootThree(Srv server, out Dictionary<short, short?> ships, out short[] ids)
    {
        server.Boot(Config());
        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p173_subject", "p173_observer", "p173_enemy" };
        for (int i = 0; i < bots.Length; i++)
        {
            bots[i].Connect(server.BoundPort, names[i]);
            bots[i].SendLogonRequest(Request(names[i]));
        }
        PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 9000);
        Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");
        ships = server.Ships.ToDictionary(s => s.Ship.Id, s => s.Ship.SideId);
        ids = bots.Select(b => b.ShipAssignment!.Ship.ShipId).ToArray();
        return bots;
    }

    /// <summary>⭐ Mounting a part broadcasts LOADOUT_CHANGE(96) to the subject's
    /// own peer and to a same-side teammate (who holds the subject's ExportShip),
    /// carrying the correct hull id + the mounted part; removing it broadcasts one
    /// reflecting the loadout without it; the cross-side enemy receives neither.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy()
    {
        Require.Artwork();
        using var server = new Srv();
        var bots = BootThree(server, out var ships, out var ids);
        try
        {
            var (si, oi, ei) = Roles(bots, ships, ids);
            short subjectId = ids[si];
            var subBot = bots[si];
            var obsBot = bots[oi];
            var enemyBot = bots[ei];

            var subject = server.Ships.First(l => l.Ship.Id == subjectId).Ship;
            var mission = subject.Mission ?? throw new InvalidOperationException("subject must have a mission");
            var weapon = RealWeapon(mission);
            const byte mount = 200;    // a deliberately unusual slot — deterministic, unoccupied

            // Gate precondition: the same-side observer must already hold the
            // subject's EXPORT, else the server (correctly) withholds the teammate send.
            PumpUntil(server, () => obsBot.Exports.Any(e => e.ShipId == subjectId), 8000);
            Assert.Contains(obsBot.Exports, e => e.ShipId == subjectId);

            // Clear any loadout-changes sent during spawn before the real action.
            subBot.ResetLoadoutChanges(); obsBot.ResetLoadoutChanges(); enemyBot.ResetLoadoutChanges();

            // (1) MOUNT: subject's own peer + same-side teammate receive a
            // LOADOUT_CHANGE with the correct hull id and the mounted part.
            subject.MountPart(weapon, 10, mount, ExpandedPartData.FullyMounted);
            PumpUntil(server, () =>
                obsBot.LoadoutChanges.Any(m =>
                    m.SidShip == subjectId
                    && m.HullId == subject.HullType.HullId
                    && m.Parts.Any(p => p.PartId == weapon.PartId && p.MountId == mount)),
                6000);
            Assert.Contains(obsBot.LoadoutChanges, m => m.SidShip == subjectId && m.HullId == subject.HullType.HullId && m.Parts.Any(p => p.PartId == weapon.PartId && p.MountId == mount));
            Assert.Contains(subBot.LoadoutChanges, m => m.SidShip == subjectId && m.Parts.Any(p => p.PartId == weapon.PartId && p.MountId == mount));
            Assert.Empty(enemyBot.LoadoutChanges);

            // (2) REMOVE: the follow-up broadcast reflects the loadout WITHOUT that
            // part, still naming the same ship/hull.
            bool removed = subject.TryRemovePart(EquipmentType.Weapon, mount, out _);
            Assert.True(removed, "TryRemovePart must succeed on the just-mounted part");
            PumpUntil(server, () =>
                obsBot.LoadoutChanges.Any(m => m.SidShip == subjectId && !m.Parts.Any(p => p.PartId == weapon.PartId)),
                6000);
            Assert.Contains(obsBot.LoadoutChanges, m => m.SidShip == subjectId && !m.Parts.Any(p => p.PartId == weapon.PartId));
            Assert.Empty(enemyBot.LoadoutChanges);
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }

    /// <summary>⭐ A gunner boarding a ship broadcasts a LOADOUT_CHANGE on the
    /// PARENT ship whose Passengers now includes the boarder's ship id + turret id;
    /// the gunner leaving broadcasts one whose Passengers no longer includes it.
    /// The cross-side enemy receives neither.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Boarding_and_leaving_broadcast_passenger_change_on_parent_but_not_enemy()
    {
        Require.Artwork();
        using var server = new Srv();
        var bots = BootThree(server, out var ships, out var ids);
        try
        {
            var (si, oi, ei) = Roles(bots, ships, ids);
            short subjectId = ids[si];        // the PARENT
            short boarderId = ids[oi];        // same-side ship that boards as the gunner
            var subBot = bots[si];
            var boarderBot = bots[oi];
            var enemyBot = bots[ei];

            var parent = server.Ships.First(l => l.Ship.Id == subjectId).Ship;
            var gunner = server.Ships.First(l => l.Ship.Id == boarderId).Ship;
            const short turret = 2;

            // Same-side gate: the boarder must hold the parent's export before the
            // server would send it the parent's LOADOUT_CHANGE as a teammate.
            PumpUntil(server, () => boarderBot.Exports.Any(e => e.ShipId == subjectId), 8000);
            Assert.Contains(boarderBot.Exports, e => e.ShipId == subjectId);

            subBot.ResetLoadoutChanges(); boarderBot.ResetLoadoutChanges(); enemyBot.ResetLoadoutChanges();

            // (1) BOARD: gunner boards the parent → AddPassenger on the PARENT.
            gunner.ParentShip = parent;
            gunner.TurretId = turret;
            parent.AddChildShip(gunner);
            PumpUntil(server, () =>
                boarderBot.LoadoutChanges.Any(m =>
                    m.SidShip == subjectId
                    && m.Passengers.Any(p => p.ShipId == boarderId && p.TurretId == turret))
                && subBot.LoadoutChanges.Any(m =>
                    m.SidShip == subjectId && m.Passengers.Any(p => p.ShipId == boarderId)),
                20000);
            Assert.Contains(boarderBot.LoadoutChanges, m =>
                m.SidShip == subjectId && m.Passengers.Any(p => p.ShipId == boarderId && p.TurretId == turret));
            Assert.Contains(subBot.LoadoutChanges, m =>
                m.SidShip == subjectId && m.Passengers.Any(p => p.ShipId == boarderId));
            Assert.Empty(enemyBot.LoadoutChanges);

            // (2) LEAVE: gunner disembarks → RemovePassenger on the PARENT.
            parent.RemoveChildShip(gunner);
            gunner.ParentShip = null;
            gunner.TurretId = null;
            PumpUntil(server, () =>
                boarderBot.LoadoutChanges.Any(m =>
                    m.SidShip == subjectId && !m.Passengers.Any(p => p.ShipId == boarderId)),
                6000);
            Assert.Contains(boarderBot.LoadoutChanges, m =>
                m.SidShip == subjectId && !m.Passengers.Any(p => p.ShipId == boarderId));
            Assert.Empty(enemyBot.LoadoutChanges);
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }
}
