using System.Collections;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P442 — real-loopback proofs that the MonoGame client's per-player wallet
/// (<see cref="State.ClientWorld.MyMoney"/>) is driven by the server over the
/// real wire, with exactly the native appmsg.cpp semantics. Each test boots an
/// in-process <see cref="Srv"/>, drives real <see cref="ConnectionSession"/>s
/// over loopback UDP, and asserts against <see cref="ClientWorld"/> — the thing
/// the HUD reads. The pure derivation rules are pinned in
/// <see cref="ClientWorldMoneyTests"/>; these prove the WIRE round-trips:
/// 1. a joiner's MyMoney is its own wallet, not the side treasury;
/// 2. a payday credits the leader dMoney×(donors+1), donors stay flat, a
///    non-donor gets dMoney — matching the server's own wallet credits;
/// 3. a bucket donation decrements the donor's client MyMoney by exactly the
///    spent amount, once.
/// Every one is red when the wiring is removed.
/// </summary>
[Collection("LiveNetwork")]
public class ClientWalletLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Client Wallet Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 944u, DwCookie = 0x442u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 94900000000000000ul, DrmHash = "cliwallet",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // ── server internals (Server.Ships is internal-invisible to this assembly) ──
    private static readonly FieldInfo PeerShipsField =
        typeof(Srv).GetField("_peerShips", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static IDictionary PeerShips(Srv server) => (IDictionary)PeerShipsField.GetValue(server)!;

    private static object LiveOf(Srv server, short shipId)
    {
        foreach (DictionaryEntry e in PeerShips(server))
        {
            var live = e.Value!;
            var ship = (Ship)live.GetType().GetProperty("Ship")!.GetValue(live)!;
            if (ship.Id == shipId) return live;
        }
        throw new InvalidOperationException($"peer ship {shipId} not found");
    }

    private static Ship ShipOf(Srv server, short id) =>
        (Ship)LiveOf(server, id).GetType().GetProperty("Ship")!.GetValue(LiveOf(server, id))!;

    private static void Fund(Srv server, short id, int amount)
    {
        var live = LiveOf(server, id);
        var wallet = live.GetType().GetProperty("Wallet")!.GetValue(live)!;
        wallet.GetType().GetProperty("Money")!.SetValue(wallet, amount);
    }

    private static int WalletOf(Srv server, short id)
    {
        var live = LiveOf(server, id);
        var wallet = live.GetType().GetProperty("Wallet")!.GetValue(live)!;
        return (int)wallet.GetType().GetProperty("Money")!.GetValue(wallet)!;
    }

    /// <summary>Establish a donate relationship server-side with a real
    /// AUTODONATE(116) broadcast to the side (so every client learns it too),
    /// via reflection into Server.SetAutoDonate (isolating the cycle algorithm
    /// from the wire-dispatch election tail, as the Core.Tests do).</summary>
    private static void CallSetAutoDonate(Srv server, short donorId, short? targetId)
    {
        var m = typeof(Srv).GetMethod("SetAutoDonate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? target = targetId is { } tid ? ShipOf(server, tid) : null;
        m.Invoke(server, new object?[] { LiveOf(server, donorId), target, 0, true });
    }

    /// <summary>Run the real payday distribution function once via reflection,
    /// after seeding side.Money — broadcasts PAYDAY(114) to the side's peers
    /// and credits the server wallets exactly as the periodic payday does.</summary>
    private static void InvokeOnPayday(Srv server, short sideId, int income)
    {
        var side = server.Missions[0].Sides.First(s => s.Id == sideId);
        var m = typeof(Srv).GetMethod("OnPayday", BindingFlags.Instance | BindingFlags.NonPublic)!;
        m.Invoke(server, new object[] { side, (float)income });
    }

    // ── spawn all players docked at the first friendly station (one side) ──
    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> s) =>
        ShipSpawnerField.SetValue(server, s);

    private static (Ship Ship, Sector Sector) SpawnSameSide(
        StaticCore core, Mission mission, short shipId)
    {
        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var st = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId >= 0);
        var sector = mission.Sectors.First(s => s.Stations.Contains(st));
        var ship = new Ship(shipId, hull,
            new Vector3(st.Position[0] + 20f + shipId * 6f, st.Position[1], st.Position[2]),
            Vector3.Zero, Orientation.Identity) { SideId = st.SideId };
        mission.PlaceShip(ship, sector);
        return (ship, sector);
    }

    private static ConnectionSession JoinAndSeat(Srv server, string name)
    {
        var session = new ConnectionSession();
        session.Connect(server.BoundPort, name);
        session.SendLogonRequest(Request(name));
        int n = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(n < 4000, $"{name} never seated (ship id assigned)");
        return session;
    }

    private static void ReadyToStart(Srv server, params ConnectionSession[] sessions)
    {
        foreach (var s in sessions) s.SendPlayerReady(fReady: true, shipId: s.World.MyShipId!.Value);
        int n = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 6000);
        Assert.True(n < 6000, $"mission never reached Started (stage={server.Missions[0].Stage})");
    }

    private static int? MyMoney(ConnectionSession s)
    {
        lock (s.Gate) return s.World.MyMoney;
    }

    private static IReadOnlyDictionary<short, int> Wallet(ConnectionSession s)
    {
        lock (s.Gate) return new Dictionary<short, int>(s.World.WalletByShipId);
    }

    // ── (1) Join: MyMoney is the joiner's own wallet, not the side treasury ──

    [Fact]
    public void Join_SetMoney_CarriesTheJoinersWallet_NotTheSideTreasury_Live()
    {
        using var server = new Srv();
        SetShipSpawner(server, SpawnSameSide);
        server.Boot(Config());

        // Treasury sentinels BEFORE any joiner: every side's pooled treasury is
        // large and distinct from a fresh player wallet (0). If the join-time
        // SET_MONEY wrongly carries the side treasury (pre-fix), the joiner
        // sees the sentinel — the bug the task's scope 2 removes.
        foreach (var s in server.Missions[0].Sides) s.Money = 95_444;

        using var b = JoinAndSeat(server, "cli_b");   // seats at NotStarted

        // B's join-time SET_MONEY (CompleteJoinAndLaunch) should carry B's OWN
        // wallet (a fresh player = 0), NOT the pooled side treasury (95444).
        int got = PumpUntil(server, () => { lock (b.Gate) return b.World.MyMoney is not null; }, 4000);
        Assert.True(got < 4000, "B never received its join-time SET_MONEY");

        Assert.Equal(0, MyMoney(b));                  // B's own wallet
        Assert.NotEqual(95_444, MyMoney(b));          // not the side treasury
    }

    // ── (2) Payday routes to auto-donate targets (leader/donor/non-donor) ──

    [Fact]
    public void Payday_CreditsLeader_KeepsDonorsFlat_NonDonorGetsShare_Live()
    {
        using var server = new Srv();
        SetShipSpawner(server, SpawnSameSide);
        server.Boot(Config());

        using var leader = JoinAndSeat(server, "cli_L");
        using var d1 = JoinAndSeat(server, "cli_D1");
        using var d2 = JoinAndSeat(server, "cli_D2");
        using var n = JoinAndSeat(server, "cli_N");

        short L = leader.World.MyShipId!.Value;
        short D1 = d1.World.MyShipId!.Value;
        short D2 = d2.World.MyShipId!.Value;
        short N = n.World.MyShipId!.Value;
        short sideId = leader.World.MySideId!.Value;
        Assert.Equal(sideId, d1.World.MySideId);
        Assert.Equal(sideId, d2.World.MySideId);
        Assert.Equal(sideId, n.World.MySideId);

        // Establish the relationships WITH a real AUTODONATE(116) broadcast, so
        // the server's wallets AND every client learn them. (Joined at
        // NotStarted, so the join default donate-to-leader never fired.)
        CallSetAutoDonate(server, D1, L);
        CallSetAutoDonate(server, D2, L);
        // L and N donate to no one (leave their targets null).

        // Every client must have learned D1→L and D2→L before the payday.
        int rel = PumpUntil(server, () =>
        {
            lock (leader.Gate) lock (d1.Gate) lock (d2.Gate) lock (n.Gate)
            {
                foreach (var s in new[] { leader, d1, d2, n })
                    if (s.World.AutoDonateTargetByShipId.TryGetValue(D1, out var t1) && t1 == L
                     && s.World.AutoDonateTargetByShipId.TryGetValue(D2, out var t2) && t2 == L)
                        continue;
                    else return false;
                return true;
            }
        }, 5000);
        Assert.True(rel < 5000, "donors' AUTODONATE never reached every client");

        // One controlled payday: side.Money = 4000, 4 players -> delta 1000.
        server.Missions[0].Sides.First(s => s.Id == sideId).Money = 4000;
        InvokeOnPayday(server, sideId, 4000);

        int pd = PumpUntil(server, () =>
        {
            lock (leader.Gate) lock (n.Gate)
                return leader.World.MyMoney is 3000 && n.World.MyMoney is 1000;
        }, 4000);
        Assert.True(pd < 4000,
            $"clients never reached post-payday money (L={MyMoney(leader)}, N={MyMoney(n)})");

        // Client derivation (the HUD truth):
        Assert.Equal(3000, MyMoney(leader));   // dMoney × (donors + 1) = 1000 × 3
        Assert.Equal(0, MyMoney(d1));          // a donor stays flat
        Assert.Equal(0, MyMoney(d2));          // second donor stays flat
        Assert.Equal(1000, MyMoney(n));        // a non-donor gets its own share

        // ...and it matches the server's own wallet credits exactly.
        Assert.Equal(3000, WalletOf(server, L));
        Assert.Equal(0, WalletOf(server, D1));
        Assert.Equal(0, WalletOf(server, D2));
        Assert.Equal(1000, WalletOf(server, N));
    }

    // ── (3) Bucket donation decrements the donor's client MyMoney once ─────

    [Fact]
    public void BucketDonation_DecrementsDonorClientsMyMoney_ExactlyOnce_Live()
    {
        using var server = new Srv();
        SetShipSpawner(server, SpawnSameSide);
        server.Boot(Config());

        using var donor = JoinAndSeat(server, "cli_donor");
        ReadyToStart(server, donor);          // bucket donate requires Started
        short me = donor.World.MyShipId!.Value;
        short sideId = donor.World.MySideId!.Value;

        var side = server.Missions[0].Sides.First(s => s.Id == sideId);
        var bucket = side.Buckets.First(b => b.ItemType == BucketItemType.BitShipType);
        int donate = 1000;
        Assert.True(donate < bucket.Price, $"donation {donate} must be under the bucket price {bucket.Price}");
        Fund(server, me, 100_000);           // plenty of wallet

        // The client's CURRENT view of its money is the pre-spend baseline
        // (whatever the client derived so far — the assertion is the DELTA).
        int before = MyMoney(donor)!.Value;

        donor.SendBucketDonate(bucket.Id, donate);

        int seen = PumpUntil(server,
            () => { lock (donor.Gate) return donor.World.MyMoney is { } m && m == before - donate; }, 4000);
        Assert.True(seen < 4000,
            $"donor client MyMoney never fell by exactly {donate} (before={before}, now={MyMoney(donor)})");

        // Exactly one decrement (no double count): the client landed on
        // before − donate, and the server wallet spent exactly that too.
        Assert.Equal(before - donate, MyMoney(donor));
        Assert.Equal(100_000 - donate, WalletOf(server, me));
    }
}
