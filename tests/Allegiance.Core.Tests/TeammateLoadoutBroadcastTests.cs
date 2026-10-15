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
/// The teammate-loadout gap found while building AllegianceClientCore's
/// <c>FullSessionKillIntegrationTests</c>: <c>FM_S_BUY_LOADOUT_ACK</c> (<c>Server.cs</c>
/// <c>ApplyLoadout</c> → <c>SendPeer(from)</c>) is sent ONLY to the requesting
/// peer, never to a same-side teammate — so a teammate's own mounted parts were
/// never observable in another peer's object model at all, independent of
/// <see cref="TeammatePositionBroadcastTests"/>'s position gap (a parallel but
/// distinct hole in the same P58/P62/P63 broadcast family). Fixed by giving
/// <see cref="ExportShip"/>'s <c>DataShipIGC</c> tail a real <c>PartData[]</c>
/// (previously always <c>nParts=0</c>) — populated at the teammate's initial
/// export and re-sent whenever <c>BroadcastShipStates</c>'s teammate loop
/// detects the subject's mounted parts changed since the last export sent to
/// that recipient.
/// </summary>
[Collection("LiveNetwork")]
public class TeammateLoadoutBroadcastTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    /// <summary>The first real weapon part id in the core (any Weapon subtype),
    /// same recipe <c>LoadoutRoundTripTests.RealWeapon</c> uses.</summary>
    private static short RealWeaponPartId()
    {
        var core = Core();
        foreach (var p in core.PartTypes.Values
                     .Where(p => p.EquipmentType == EquipmentType.Weapon)
                     .OrderBy(p => p.PartId))
            return p.PartId;
        throw new InvalidOperationException("no weapon in representative core");
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Teammate Loadout Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 886u, DwCookie = 0x186u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 88600000000000000ul, DrmHash = "teamloadouttest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ The real regression: a same-side teammate's real, non-hull-only
    /// loadout must reach the observer's object model as an EXPORT PartData[]
    /// tail — both the (empty) initial export and, after the subject buys real
    /// parts, a follow-up export carrying them — while a cross-side enemy's
    /// loadout must never reach the observer at all (same-side scope, matching
    /// P58/P62/P63/P85). 3 bots / 2 teams (round-robin) gives a guaranteed
    /// same-side pair and a cross-side enemy, same construction as
    /// <see cref="TeammatePositionBroadcastTests"/>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Teammates_real_loadout_is_broadcast_to_observers_but_enemy_loadout_is_not()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p86_subject", "p86_observer", "p86_enemy" };
        try
        {
            for (int i = 0; i < bots.Length; i++)
            {
                bots[i].Connect(server.BoundPort, names[i]);
                bots[i].SendLogonRequest(Request(names[i]));
            }
            PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            var ships = server.Ships.ToDictionary(s => s.Ship.Id, s => s.Ship.SideId);
            short[] ids = bots.Select(b => b.ShipAssignment!.Ship.ShipId).ToArray();

            int subject = -1, observer = -1, enemy = -1;
            for (int i = 0; i < bots.Length && observer < 0; i++)
                for (int j = 0; j < bots.Length; j++)
                    if (j != i && ships[ids[j]] == ships[ids[i]]) { subject = i; observer = j; break; }
            Assert.True(subject >= 0, "round-robin must yield a same-side pair");
            for (int k = 0; k < bots.Length; k++)
                if (k != subject && k != observer) { enemy = k; break; }
            Assert.True(enemy >= 0 && ships[ids[enemy]] != ships[ids[subject]],
                "3 bots / 2 teams must leave one bot cross-side");

            short subjectId = ids[subject];
            short enemyId = ids[enemy];
            var obsBot = bots[observer];

            // Wait for the observer to learn about the subject at all (the
            // initial, one-shot P62 export — nParts=0 at this point, nothing
            // mounted yet).
            PumpUntil(server, () => obsBot.Exports.Any(e => e.ShipId == subjectId), 4000);
            Assert.Contains(obsBot.Exports, e => e.ShipId == subjectId);
            Assert.Empty(obsBot.Exports.First(e => e.ShipId == subjectId).Parts);

            // Real, non-hull-only loadout requests on both the subject
            // (teammate) and the enemy — same weapon part, real mount/amount.
            short partId = RealWeaponPartId();
            var subjectHull = server.Ships.Single(s => s.Ship.Id == subjectId).Ship.HullType.HullId;
            var enemyHull = server.Ships.Single(s => s.Ship.Id == enemyId).Ship.HullType.HullId;
            var requested = new ExpandedPartData(partId, Amount: 20, MountId: 0, FractionMounted: ExpandedPartData.FullyMounted);
            // H-06: purchases launch from the initial dock; H-07: use the
            // actual weapon accepted by each hull's first hardpoint.
            foreach (int i in new[] { subject, enemy })
            {
                var ship = server.Ships.Single(s => s.Ship.Id == ids[i]).Ship;
                var part = ship.Mission!.Core.PartTypes[partId];
                Assert.True(ship.HullType.CanMountPart(part, 0));
                EconomyLoadoutTestSetup.Sell(ship.Mission, part);
                bots[i].SendBuyLoadout(ship.HullType.HullId, fLaunch: true, parts: new[] { requested });
            }

            // The observer must receive a SECOND export for the subject, this
            // time carrying the real mounted part.
            PumpUntil(server, () => obsBot.Exports.Any(e => e.ShipId == subjectId && e.Parts.Count > 0), 4000);
            var loadoutExports = obsBot.Exports.Where(e => e.ShipId == subjectId).ToList();
            Assert.True(loadoutExports.Count >= 2, "subject's loadout change must trigger a re-export, not just the initial one-shot EXPORT");
            var withParts = loadoutExports.Last();
            Assert.Single(withParts.Parts);
            Assert.Equal(partId, withParts.Parts[0].PartId);
            Assert.Equal((short)20, withParts.Parts[0].Amount);
            Assert.Equal((byte)0, withParts.Parts[0].MountId);

            // Same-side scope: the enemy's loadout must never reach the
            // observer at all — not even the hull-only initial export, since
            // the enemy never entered the observer's table in the first place.
            Assert.DoesNotContain(obsBot.Exports, e => e.ShipId == enemyId);
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }
}
