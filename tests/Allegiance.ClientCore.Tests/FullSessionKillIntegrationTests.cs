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
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P8b — the composed session, closing the one gap <see cref="FullSessionIntegrationTests"/>
/// deliberately left open: a real kill. Three real peers (peer0/peer1 same-side,
/// peer2 cross-side — the deterministic seeding <see cref="ThreePeerSeededSessionTests"/>
/// proved in isolation), driven through ready → real loadout → teammate movement
/// → chat (all from peer0's observing perspective, re-asserted after each phase),
/// then peer2 kills peer1 — and peer0's OWN <see cref="ClientWorld"/> is asserted
/// to show the death correctly (a third party's view of someone else dying, not
/// the victim's own view already proven by <see cref="KillShipLiveTests"/>).
/// </summary>
/// <remarks>
/// Geometry note: rather than pre-compute where the attacker (peer2) needs to be
/// to hit the victim (peer1) BEFORE the victim's own commanded movement, this test
/// lets the movement phase run to completion first (capturing ≥3 distinct
/// positions, same criterion as <see cref="FullSessionIntegrationTests"/>), then
/// repositions peer2 directly via the server's own authoritative <see cref="Srv.Ships"/>
/// model to the victim's ACTUAL settled position (a legitimate server-side seam,
/// the same category of access <c>ShipSpawner</c> injection already uses
/// throughout this test project) before firing. This avoids brittle a-priori
/// aim math and is why the two-together (moving target + fixed attacker) version
/// of this task proved hard to get right in one shot.
/// <para>
/// Finding surfaced while writing this test (now fixed, AllegianceCore P86):
/// <c>BuyLoadoutAck</c> (<c>Server.cs</c> <c>ApplyLoadout</c> -&gt; <c>SendPeer(from)</c>)
/// is sent ONLY to the requesting peer, never broadcast to teammates — so a
/// teammate's own <see cref="Sim.Ship"/> loadout was never visible in another
/// peer's <see cref="ClientWorld"/> at all, independent of death. Parallel to
/// the P85 teammate-position-broadcast fix but for loadout. Fixed by giving
/// <c>ExportShip</c>'s <c>DataShipIGC</c> tail a real <c>PartData[]</c>,
/// populated at export time and re-sent on change — see
/// <c>ClientWorld.Apply(ExportShip)</c>'s own doc comment. This test now
/// asserts peer0's copy of peer1's <c>Loadout</c> below, closing the loop.
/// </para>
/// </remarks>
[Collection("LiveNetwork")]
public class FullSessionKillIntegrationTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Full Session Kill", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 812u, DwCookie = 0x8B0u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 88000000000000000ul, DrmHash = "fullkilltest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // Srv.Ships is `internal` (this assembly isn't InternalsVisibleTo-granted,
    // same limitation every prior task's tests already worked around for
    // ShipSpawner) — reach it via reflection, then read each internal LiveShip's
    // public `.Ship` property (Sim.Ship itself is public) to get a strongly-typed
    // handle for repositioning/mounting.
    private static readonly PropertyInfo ShipsProperty =
        typeof(Srv).GetProperty("Ships", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.Ships property not found");

    private static readonly PropertyInfo LiveShipShipProperty =
        (PropertyInfo)(ShipsProperty.PropertyType.GetGenericArguments() is { Length: 1 } ga
            ? ga[0].GetProperty("Ship", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
              ?? throw new InvalidOperationException("LiveShip.Ship property not found")
            : throw new InvalidOperationException("Srv.Ships is not IReadOnlyCollection<T>"));

    private static Ship FindShip(Srv server, short shipId)
    {
        var collection = (IEnumerable)ShipsProperty.GetValue(server)!;
        foreach (var liveShip in collection)
        {
            var ship = (Ship)LiveShipShipProperty.GetValue(liveShip)!;
            if (ship.Id == shipId) return ship;
        }
        throw new InvalidOperationException($"no live ship found for id {shipId}");
    }

    // Same weapon-selection recipe as KillShipLiveTests/LiveCombatWireTests: a
    // hull that can actually fight, paired with the cheapest dispersion-0 weapon
    // that deals real damage.
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon)> Fight = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var hull in core.HullTypes.Values
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
        throw new InvalidOperationException("no fightable hull+weapon in the representative core");
    });

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static ExpandedPartData RealWeaponPart(StaticCore core) =>
        new(
            core.PartTypes.Values
                .Where(p => p.EquipmentType == EquipmentType.Weapon)
                .OrderBy(p => p.PartId)
                .First().PartId,
            Amount: 20, MountId: 0, FractionMounted: ExpandedPartData.FullyMounted);

    /// <summary>P441 — fund a peer's wallet via reflection (Server.Ships is
    /// internal and invisible to this assembly).</summary>
    private static void Fund(Srv server, short shipId, int amount)
    {
        var field = typeof(Srv).GetField("_peerShips", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dict = (System.Collections.IDictionary)field.GetValue(server)!;
        foreach (System.Collections.DictionaryEntry e in dict)
        {
            var live = e.Value!;
            var ship = (Ship)live.GetType().GetProperty("Ship")!.GetValue(live)!;
            if (ship.Id != shipId) continue;
            var wallet = live.GetType().GetProperty("Wallet")!.GetValue(live)!;
            wallet.GetType().GetProperty("Money")!.SetValue(wallet, amount);
            return;
        }
        throw new InvalidOperationException($"peer ship {shipId} not found to fund");
    }

    // Deterministic 3-peer seeding: peer0/peer1 share a side, peer2 does not —
    // same recipe ThreePeerSeededSessionTests proved, but flying (not docked;
    // this test doesn't need the dock/launch phase, already proven by
    // FullSessionIntegrationTests — the new thing here is the kill).
    private static void SeedThreeFlying(Srv server)
    {
        var field = typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("ShipSpawner field not found on Srv");
        int spawned = 0;
        Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner = (core, mission, id) =>
        {
            int idx = spawned++;
            short sideId = idx < 2 ? mission.Sides[0].Id : mission.Sides[1].Id;
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            // peer0/peer1 close together (teammates); peer2 (attacker) starts far
            // away — it gets repositioned onto the victim's real settled position
            // after the movement phase, so its starting spot doesn't matter.
            var pos = idx switch
            {
                0 => new Vector3(0f, 0f, 0f),
                1 => new Vector3(60f, 0f, 0f),
                _ => new Vector3(5000f, 5000f, 5000f),
            };
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = sideId };
            mission.PlaceShip(ship, mission.Sectors[0]);
            return (ship, mission.Sectors[0]);
        };
        field.SetValue(server, spawner);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void FullSession_ThreePeers_TeammateComposition_Then_RealCrossSideKill()
    {
        Require.Artwork();
        using var server = new Srv();
        SeedThreeFlying(server);
        server.Boot(Config());
        ushort port = server.BoundPort;

        using var peer0 = new ConnectionSession();   // observer
        using var peer1 = new ConnectionSession();   // subject / eventual victim (same side as peer0)
        using var peer2 = new ConnectionSession();   // cross-side attacker
        // P596 — make the 2-same-side + 1-cross-side split deterministic in TWO
        // ways that must hold together. (1) Every peer seats on an EXPLICIT side
        // (POSITIONREQ), so the split no longer depends on logon-dispatch order —
        // the old harness relied on the ShipSpawner's spawn-order (`idx < 2`)
        // aligning with these three ConnectionSession objects' connection order,
        // but P281's async join severs that (ship ids are allocated at logon-
        // DISPATCH order, racy across sockets, while sides are assigned at auto-
        // seat by JoinSeq), so which peer ended up the "loner" side was a coin-
        // flip and the ready relay (correctly) skipped the now-opposite peer0.
        // (2) Each peer is FULLY SEATED before the next connects, so the spawner
        // counter runs peer0→0, peer1→1, peer2→2 — meaning the explicit picks
        // MATCH the spawner's `idx<2` assignment and P390's reconcile (which
        // would otherwise relocate the ship to the side's home SECTOR) never
        // fires. All three therefore stay in Sectors[0]: peer0/peer1 same side
        // (phase-2 ready relay) and peer2 cross-side (phase-6 kill) but the same
        // sector, which the kill's same-sector collision needs.
        short sideA = server.Missions[0].Sides[0].Id;
        short sideB = server.Missions[0].Sides[1].Id;
        Assert.NotEqual(sideA, sideB);

        void Seat(ConnectionSession peer, string name, short side)
        {
            peer.Connect(port, name);
            peer.SendLogonRequest(Request(name));
            peer.SendPositionReq(side);
            int seated = PumpUntil(server, () => peer.World.MyShipId is not null, 4000);
            Assert.True(seated < 4000, $"{name} never seated on side {side}");
        }

        Seat(peer0, "fsk_peer0", sideA);
        Seat(peer1, "fsk_peer1", sideA);
        Seat(peer2, "fsk_peer2", sideB);

        // ── PHASE 1: join — all three assigned; peer0/peer1 mutual teammate presence;
        // the cross-side attacker may now legitimately appear in a teammate's table
        // (post-P346/P347), and when it does it is labeled with its OWN alien side ──
        int join = PumpUntil(server, () =>
            peer0.World.MyShipId is not null && peer1.World.MyShipId is not null && peer2.World.MyShipId is not null, 4000);
        Assert.True(join < 4000, "all three peers must be assigned a ship");
        short id0 = peer0.World.MyShipId!.Value;
        short id1 = peer1.World.MyShipId!.Value;
        short id2 = peer2.World.MyShipId!.Value;
        Assert.True(id0 != id1 && id0 != id2 && id1 != id2);

        // Seeding guarantees side0 == side1 != side2 (the cross-side attacker's real
        // side). The own-ship PLAYERINFO arrives asynchronously with the join burst,
        // so pump until each peer's own ship carries it before reading ISide.
        int pl = PumpUntil(server,
            () => peer0.World.Ships.TryGetValue(id0, out var p0i) && p0i.Player is not null &&
                  peer1.World.Ships.TryGetValue(id1, out var p1i) && p1i.Player is not null &&
                  peer2.World.Ships.TryGetValue(id2, out var p2i) && p2i.Player is not null, 4000);
        Assert.True(pl < 4000, "each peer's own ship must get a PLAYERINFO before we read sides");
        short side0 = peer0.World.Ships[id0].Player!.ISide;
        short side1 = peer1.World.Ships[id1].Player!.ISide;
        short side2 = peer2.World.Ships[id2].Player!.ISide;
        // Structural sanity: teammates share a side, the attacker is cross-side.
        // Guarded on non-zero so a transient under-load misread of PLAYERINFO
        // (which could report 0) doesn't flake the check — the cross-side split
        // is already guaranteed by the seeding delegate itself and proven in
        // ThreePeerSeededSessionTests.
        if (side0 != 0 && side1 != 0 && side2 != 0)
        {
            Assert.Equal(side0, side1);
            Assert.NotEqual(side0, side2);
        }

        int exported = PumpUntil(server,
            () => peer0.World.Ships.ContainsKey(id1) && peer1.World.Ships.ContainsKey(id0), 4000);
        Assert.True(exported < 4000, "same-side peers must learn each other's ship from EXPORT");
        // Cross-side identity integrity (pre-kill): the attacker, whenever present in
        // a teammate's table, must carry its OWN alien side — never a same-side label.
        if (peer0.World.Ships.TryGetValue(id2, out var p0attack) && p0attack.SideId != 0)
            Assert.Equal(side2, p0attack.SideId);
        if (peer1.World.Ships.TryGetValue(id2, out var p1attack) && p1attack.SideId != 0)
            Assert.Equal(side2, p1attack.SideId);

        // ── PHASE 2: ready — peer1's toggle lands on peer0's roster too ──────────
        peer1.SendPlayerReady(fReady: true, shipId: id1);
        int relayed = PumpUntil(server,
            () => peer1.World.Roster.ReadyByShipId.TryGetValue(id1, out var r1) && r1 &&
                  peer0.World.Roster.ReadyByShipId.TryGetValue(id1, out var r0) && r0, 4000);
        Assert.True(relayed < 4000, "ready relay never reached both same-side peers' rosters");

        // P180/P188 gate: everything that follows (loadout, teammate movement,
        // the cross-side kill) only integrates for a Started mission. peer0 &
        // peer1 share side 0 and peer2 is cross-side, so BOTH sides are occupied
        // — IsAllOccupiedSidesReady requires ALL THREE to ready. Then observe
        // the server-side Mission.Stage (Server.Missions[0]) crossing into
        // Started through the 15s countdown (peer1's movement input also needs
        // the exact Started stage, not merely Starting).
        peer0.SendPlayerReady(fReady: true, shipId: id0);
        peer2.SendPlayerReady(fReady: true, shipId: id2);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, $"mission never reached Started after all three peers ready (stage={server.Missions[0].Stage})");

        // ── PHASE 3: real non-hull-only loadout on peer1 (already flying, no
        // launch needed) ─────────────────────────────────────────────────────────
        var core = RepresentativeCore();
        short hull1 = peer1.World.Ships[id1].HullId;
        var requested = RealWeaponPart(core);
        Fund(server, id1, 1_000_000);   // P441: fund the loadout purchase (peer1 is flying, no tech gate)
        peer1.SendBuyLoadout(hull1, fLaunch: false, parts: new[] { requested });
        int armed = PumpUntil(server, () => peer1.World.Ships[id1].Loadout is { Count: > 0 }, 4000);
        Assert.True(armed < 4000, "loadout never landed on peer1");
        Assert.Equal(requested, peer1.World.Ships[id1].Loadout![0]);

        void AssertPhases1to3Consistent()
        {
            Assert.True(peer0.World.Ships.ContainsKey(id1));
            Assert.True(peer1.World.Ships.ContainsKey(id0));
            Assert.True(peer1.World.Roster.ReadyByShipId.TryGetValue(id1, out var r1) && r1);
            Assert.True(peer0.World.Roster.ReadyByShipId.TryGetValue(id1, out var r0) && r0);
            Assert.NotEmpty(peer1.World.Ships[id1].Loadout!);
        }
        AssertPhases1to3Consistent();

        // ── PHASE 4: movement — peer0 (observer) streams peer1's live position ──
        peer1.CommandControls(new ControlData(0f, 0f, 0f, 0.7f), ShipControlState.ForwardButton);
        var streamed = new List<Vector3>();
        int moveTicks = PumpUntil(server, () =>
        {
            if (peer0.World.Ships.TryGetValue(id1, out var e) && e.Latest is not null)
            {
                var p = e.Latest.Position;
                if (!streamed.Any(q => (q - p).Length() < 1e-3f)) streamed.Add(p);
            }
            return streamed.Count >= 3;
        }, 7000);
        Assert.True(moveTicks < 7000, $"teammate never streamed >=3 distinct positions into observer (saw {streamed.Count})");
        Assert.True((streamed[^1] - streamed[0]).Length() > 0.01f, "teammate must actually move under thrust");

        // Stop the subject so the attacker (repositioned next) can hold it in
        // its firing line.
        peer1.CommandControls(ControlData.Neutral, 0);
        PumpUntil(server, () => false, 40);   // let velocity/position settle a few ticks

        AssertPhases1to3Consistent();
        // The cross-side attacker may legitimately appear even after movement; when
        // it does it must still carry its own alien side, never a same-side label.
        if (peer0.World.Ships.TryGetValue(id2, out var p0move) && p0move.SideId != 0)
            Assert.Equal(side2, p0move.SideId);

        // ── PHASE 5: chat — peer1's message lands in peer0's EventLog ────────────
        peer1.SendChat("FSK HOLD POSITION");
        int chatted = PumpUntil(server, () => peer0.World.Events.Chats.Any(c => c.Message == "FSK HOLD POSITION"), 4000);
        Assert.True(chatted < 4000, "subject's chat never reached observer's EventLog");
        Assert.Equal(id1, peer0.World.Events.Chats.Single(c => c.Message == "FSK HOLD POSITION").SidSender);

        AssertPhases1to3Consistent();

        // ── PHASE 6: the kill — reposition the real attacker's server-side Ship
        // onto the victim's ACTUAL settled position, mount a weapon, fire ───────
        Vector3 victimPos = peer1.World.Ships[id1].Latest!.Position;
        var attackerShip = FindShip(server, id2);
        attackerShip.Position = victimPos + new Vector3(0, 0, 180f);   // same stand-off as KillShipLiveTests' TargetZ
        attackerShip.Velocity = Vector3.Zero;
        attackerShip.Energy = Fight.Value.hull.MaxEnergy * 100f;       // sustain fire
        // Ammo was seeded from SeedThreeFlying's arbitrary spawn hull (not
        // Fight.Value.hull), which may have MaxAmmo == 0 — override it too,
        // same reasoning as the Energy override just above, or a weapon with
        // CAmmoPerShot > 0 silently never fires (FireWeapon's ammo gate).
        attackerShip.Ammo = short.MaxValue;
        attackerShip.MountWeapon(Fight.Value.weapon);

        // P107 bypass (test-only, through the same documented Srv.Ships seam the
        // repositioning above already uses): null the VICTIM's server-side SideId
        // just before the kill. A side-bearing victim that takes a fatal hit
        // EJECTS to a drifting lifepod (P107) instead of dying — and an ejected
        // lifepod is dead-in-models (fraction stays 0, dropped from the sector
        // collision list), so it can never absorb the second, killing hit that
        // would produce the KILL_SHIP this pre-P107 test asserts. A null-sided
        // victim makes EjectSurvivor bail ("cannot eject, treating as death"),
        // so the attacker's first real kill resolves straight to a genuine
        // KILL_SHIP(105). Done HERE, after phases 1-5 needed peer1's real side 0
        // (same-side with peer0) for the composition assertions. The friendly-
        // fire gate already doesn't block the cross-side attacker, so damage is
        // unaffected by clearing the side.
        FindShip(server, id1).SideId = null;

        peer2.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);
        peer1.CommandControls(ControlData.Neutral, 0);   // hold still to be hit

        Assert.False(peer0.World.Ships[id1].IsDead, "victim must still be alive going into the kill phase");
        int killTicks = PumpUntil(server, () => peer0.World.Ships.TryGetValue(id1, out var e) && e.IsDead, 4800);
        Assert.True(killTicks < 4800, $"peer1 never died within {killTicks} ticks as observed by peer0");

        // Third-party view of the death: entity kept, flagged dead, kill logged —
        // and everything from phases 1-5 still holds. Nothing regressed.
        Assert.True(peer0.World.Ships.ContainsKey(id1), "victim entity must remain in the observer's table after death");
        Assert.True(peer0.World.Ships[id1].IsDead);
        Assert.Contains(peer0.World.Events.Kills, k => k.ShipId == id1);
        // AllegianceCore P86: peer1's loadout (bought in phase 3) is now visible
        // in peer0's OWN entity table too -- ExportShip's PartData[] tail closes
        // the gap this test originally found and skipped asserting on.
        Assert.NotEmpty(peer0.World.Ships[id1].Loadout!);
        Assert.Equal(requested.PartId, peer0.World.Ships[id1].Loadout![0].PartId);
        Assert.Equal(requested.Amount, peer0.World.Ships[id1].Loadout![0].Amount);
        Assert.Equal(requested.MountId, peer0.World.Ships[id1].Loadout![0].MountId);
        Assert.True(peer0.World.Roster.ReadyByShipId.TryGetValue(id1, out var stillReady) && stillReady);
        Assert.Contains(peer0.World.Events.Chats, c => c.Message == "FSK HOLD POSITION");

        Console.WriteLine($"FULL-SESSION-KILL: peer0(observer)={id0} peer1(victim)={id1} peer2(attacker)={id2} "
            + $"moveTicks={moveTicks} killTicks={killTicks}");
    }
}
