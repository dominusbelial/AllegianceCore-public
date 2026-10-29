using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P604 — the native deviation-ranked ship-update budget model
/// (<c>broadcast.shipUpdateModel: "native"</c> — the default since P887).
/// Port mode sends
/// every dirty same-side teammate's position every tick as a per-tick
/// <c>posBatch</c> SingleShipUpdate(78) stream — the O(N²) cost P600 measured at
/// ~58% of the tick (9.5 ms @ 241 players). Native mode suppresses that stream:
/// remote-ship positions travel ONLY through the deviation-ranked
/// HEAVY/LIGHT_SHIPS_UPDATE(176/175) budget (the P219 path extended to a real
/// two-tier split), while the P608 owner's own-ship stream stays reliable.
///
/// These tests drive <see cref="Srv.BroadcastShipStates"/> /
/// <see cref="Srv.BroadcastShipsUpdate"/> directly via the internal seams
/// (deterministic, no live clients), asserting the five verification contracts:
/// <list type="number">
/// <item>the flag defaults to <c>"port"</c>;</item>
/// <item>native mode sends NO <c>posBatch</c> (no <c>CoalesceShipStates</c>=1
/// frame) yet still delivers the owner's own-ship <c>CoalesceOwnShip</c>=12;</item>
/// <item>native deviation bookkeeping matches native — heavy-served reset to 0,
/// light-served +0.70, skipped +1.00;</item>
/// <item>native sends at most the budgeted heavy (2) + light (2) per peer per tick;</item>
/// <item>port mode still sends the <c>posBatch</c> (contrast proving the gate).</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public sealed class NativeShipUpdateModelTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Broadcast = new() { MinUpdateHz = 0 }, // tests the pre-floor ranked budget

        Games =
        [
            new Gm { Name = "Native Ship Update Model Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static short _nextFakeShipId = 30000;

    /// <summary>Decode a coalesced CoalesceShipsUpdate frame (a FedPacket that may
    /// carry a HEAVY(176) and/or a LIGHT(175) fedmsg) into (heavyCount, lightCount).</summary>
    private static (int Heavy, int Light) CountShipsUpdate(byte[] pkt)
    {
        int h = 0, l = 0;
        foreach (var msg in FedPacket.Unpack(pkt))
        {
            ushort fmid = (ushort)(msg[2] | (msg[3] << 8));   // FedMessageHeader: cbmsg@0, fmid@2
            if (fmid == HeavyShipsUpdate.Fmid) h += HeavyShipsUpdate.Decode(msg).HeavyShipEntries.Length;
            else if (fmid == LightShipsUpdate.Fmid) l += LightShipsUpdate.Decode(msg).LightShipEntries.Length;
        }
        return (h, l);
    }

    /// <summary>⭐ Contract 1 — the flag defaults to "native" since P887 (the
    /// rig baseline) when no config/env selects otherwise; an explicit
    /// "port" still selects the old path.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Ship_update_model_defaults_to_native()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        Assert.True(server._nativeShipUpdates, "no config/env → must default to native");
        Assert.Equal("native", server._shipUpdateModel);
    }

    /// <summary>A fabricated world with <paramref name="peerCount"/> same-side
    /// (side 0) peer ships all in <c>Sectors[0]</c> (fresh ships ⇒ dirty ⇒ the
    /// posBatch would fire in port mode), plus <paramref name="npcCount"/>
    /// cross-side NPCs in the same sector (so the heavy/light budget has a
    /// populated candidate list). Returns the seeded peer list and the NPCs.</summary>
    private static (List<(ConnectionId Conn, Ship Ship, Sector Sector)> Peers, List<Ship> Npcs)
        BuildWorld(Srv server, DataHullTypeIGC hull, int peerCount, int npcCount)
    {
        var sect0 = server.TestSectors[0];
        var peers = new List<(ConnectionId, Ship, Sector)>();
        for (int i = 0; i < peerCount; i++)
        {
            var ship = new Ship(_nextFakeShipId++, hull, new Vector3(i * 25.0f, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            server.TestMission!.PlaceShip(ship, sect0);
            peers.Add((new ConnectionId((uint)(700 + i)), ship, sect0));
        }
        var npcs = new List<Ship>();
        for (int i = 0; i < npcCount; i++)
        {
            var npc = new Ship(_nextFakeShipId++, hull, new Vector3(300f + i * 40f, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 1 };
            server.TestMission!.PlaceShip(npc, sect0);
            npcs.Add(npc);
        }
        return (peers, npcs);
    }

    /// <summary>⭐ Contracts 2 &amp; 5 — native mode emits NO per-tick teammate
    /// posBatch (no CoalesceShipStates=1 frame) while port mode DOES, and the
    /// owner's own-ship CoalesceOwnShip=12 frame is still delivered in both.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Native_mode_suppresses_posBatch_but_keeps_the_owner_own_ship_stream()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        var (peers, _) = BuildWorld(server, Core().HullTypes.Values.First(), peerCount: 2, npcCount: 0);
        Assert.True(peers.Count >= 2, "need ≥ 2 same-side peers for the posBatch");

        // Port mode: every same-side mate's position flows per tick → a
        // CoalesceShipStates(=1) frame reaches the observer, alongside the owner
        // own-ship CoalesceOwnShip(=12) frame.
        server.TestResetBroadcastState();
        server.TestSeedPeers(peers);
        server.TestSetShipUpdateModel(false);
        var portKeys = server.TestCollectBroadcastKeys(0, parallel: false, step: "shipStates");
        var portBatch = portKeys.Values.SelectMany(v => v).Count(f => f.Key == 1);
        var portOwn = portKeys.Values.SelectMany(v => v).Count(f => f.Key == 12);
        Assert.True(portBatch > 0, "port mode MUST emit the per-tick teammate posBatch (CoalesceShipStates)");
        Assert.True(portOwn > 0, "port mode MUST deliver the owner's own-ship CoalesceOwnShip frame");

        // Native mode: the posBatch is gone; the owner own-ship frame remains.
        server.TestResetBroadcastState();
        server.TestSeedPeers(peers);
        server.TestSetShipUpdateModel(true);
        var nativeKeys = server.TestCollectBroadcastKeys(0, parallel: false, step: "shipStates");
        var nativeBatch = nativeKeys.Values.SelectMany(v => v).Count(f => f.Key == 1);
        var nativeOwn = nativeKeys.Values.SelectMany(v => v).Count(f => f.Key == 12);
        Assert.True(nativeBatch == 0, "native mode must NOT send the per-tick teammate posBatch (no CoalesceShipStates frame)");
        Assert.True(nativeOwn > 0, "native mode MUST still deliver the owner's own-ship CoalesceOwnShip frame");
    }

    /// <summary>⭐ Contract 3 — native deviation bookkeeping matches native: after
    /// one budget-path broadcast over 5 same-sector NPCs (6 candidates incl. the
    /// peer), exactly 2 are heavy-served (reset 0), exactly 2 light-served (+0.70),
    /// exactly 1 skipped (+1.00) — the bands at FedSrv.CPP:3937-3950.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Native_mode_deviation_bands_match_native()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        var (peers, npcs) = BuildWorld(server, Core().HullTypes.Values.First(), peerCount: 1, npcCount: 5);
        Assert.Equal(5, npcs.Count);

        server.TestResetBroadcastState();
        server.TestSeedPeers(peers);
        server.TestSetShipUpdateModel(true);
        server.TestCollectBroadcastKeys(0, parallel: false, step: "shipsUpdate");

        int reset0 = 0, light070 = 0, skip100 = 0;
        foreach (var n in npcs)
        {
            if (n.Deviation == 0f) reset0++;
            else if (MathF.Abs(n.Deviation - 0.70f) < 1e-6f) light070++;
            else if (MathF.Abs(n.Deviation - 1.00f) < 1e-6f) skip100++;
        }
        Assert.Equal(2, reset0);   // heavy-served → reset (FedSrv.CPP:3939-3940)
        Assert.Equal(2, light070); // light-served → +0.70 (FedSrv.CPP:3943-3948)
        Assert.Equal(1, skip100);  // skipped → +1.00
    }

    /// <summary>⭐ Contract 4 — native mode sends at most the budgeted heavy (2)
    /// + light (2) updates per peer per tick — never proportional to ship count.
    /// With 6 candidates (peer + 5 NPCs) the total is exactly 4 (2 heavy + 2
    /// light), never the full 5 others.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Native_mode_sends_at_most_the_budgeted_heavy_and_light_per_peer_per_tick()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        var (peers, npcs) = BuildWorld(server, Core().HullTypes.Values.First(), peerCount: 1, npcCount: 5);
        Assert.True(npcs.Count > 4, "sanity: 5 others would be sent if the budget were absent");

        server.TestResetBroadcastState();
        server.TestSeedPeers(peers);
        server.TestSetShipUpdateModel(true);
        var keys = server.TestCollectBroadcastKeys(0, parallel: false, step: "shipsUpdate");

        var peerConn = peers[0].Conn;
        Assert.True(keys.ContainsKey(peerConn), "the peer must receive its ships-update frame");
        var (heavy, light) = (0, 0);
        foreach (var (pkt, key, _) in keys[peerConn])
        {
            Assert.Equal(2, key);   // CoalesceShipsUpdate
            var (h, l) = CountShipsUpdate(pkt);
            heavy += h; light += l;
        }
        Assert.Equal(2, heavy);   // ≤ cHeavyPerInterval
        Assert.Equal(2, light);   // ≤ cLightPerInterval
        Assert.True(heavy + light <= 4, "total ≤ heavy+light budget = 4");
        Console.WriteLine($"P604-NATIVE-BOUND: peer sees {heavy} heavy + {light} light = {heavy + light} records/tick " +
                          $"with {npcs.Count + 1} ships in the cluster (never the {npcs.Count} others)");
    }

    /// <summary>⭐ P604b — the native heavy budget is DENSITY-AWARE: at high
    /// in-sector density it raises the heavy quota so every rendered ship is
    /// re-fed within the client dead-reckoner's 1.5 s smooth horizon
    /// (DeadReckonExtrapolationTicks=30), instead of native's fixed 2 heavy/tick
    /// (which at ~195 ships re-feeds each ship only every ~4.9 s → the renderer-
    /// only client HOLDS the pose = the visible freeze on the 221 195v195 run).
    /// The quota is ⌈others/30⌉ (≥ native's 2). At 120 in-sector others the
    /// peer must receive ≥ 4 heavy poses/tick (pre-fix: 2).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Native_mode_heavy_budget_scales_with_density_for_dead_reckoner_horizon()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        const int npcCount = 120;   // 120 in-sector others → ⌈120/30⌉ = 4 heavy/tick
        var (peers, npcs) = BuildWorld(server, Core().HullTypes.Values.First(), peerCount: 1, npcCount);
        Assert.Equal(npcCount, npcs.Count);

        server.TestResetBroadcastState();
        server.TestSeedPeers(peers);
        server.TestSetShipUpdateModel(true);
        var keys = server.TestCollectBroadcastKeys(0, parallel: false, step: "shipsUpdate");

        var peerConn = peers[0].Conn;
        Assert.True(keys.ContainsKey(peerConn), "the peer must receive its ships-update frame");
        var (heavy, light) = (0, 0);
        foreach (var (pkt, key, _) in keys[peerConn])
        {
            Assert.Equal(2, key);   // CoalesceShipsUpdate
            var (h, l) = CountShipsUpdate(pkt);
            heavy += h; light += l;
        }
        int expectedHeavy = Math.Max(2, (int)Math.Ceiling(npcCount / 30d));   // 30 = DeadReckonExtrapolationTicks (1.5 s @ 20 tps)
        Assert.True(heavy >= expectedHeavy,
            $"at {npcCount} in-sector others, native must serve ≥ {expectedHeavy} heavy/tick to stay within the " +
            $"1.5 s dead-reckoner horizon (got {heavy}) — the fixed budget-2 would freeze ships");
        Console.WriteLine($"P604b-DENSITY: at {npcCount} in-sector others, peer gets {heavy} heavy + {light} light " +
                          $"/tick (⌈N/30⌉={expectedHeavy}), keeping every ship's pose interval ≤ 1.5 s");
    }

    /// <summary>⭐ P632 — the SAME density-aware heavy budget must apply to PORT
    /// mode for CROSS-SIDE (enemy) ships. Port mode's per-tick <c>posBatch</c>
    /// covers only TEAMMATES; an enemy's live pose reaches a bot ONLY through the
    /// HEAVY_SHIPS_UPDATE budget. With the fixed 2-heavy(+2 folded-light)=4
    /// serves/tick, ~195 co-located enemies are each re-fed only every ~49 ticks
    /// — far beyond the client dead-reckoner's 1.5 s horizon
    /// (DeadReckonExtrapolationTicks=30) — so enemy poses freeze, bots cannot
    /// acquire/lead/fire, and a 195v195 port run produces 0 kills (measured at
    /// BOTH 9194ddec and e1175e1b). The fix extends the P604b density-aware quota
    /// to port mode, so an enemy is re-fed within the horizon. Fails pre-fix
    /// (port serves 4 heavy/tick → heavy &lt; ⌈195/30⌉=7).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Port_mode_heavy_budget_scales_with_density_for_enemy_dead_reckoner_horizon()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        const int npcCount = 195;   // 195 in-sector CROSS-SIDE enemies → ⌈195/30⌉ = 7 heavy/tick (pre-fix port serves only 2+2 folded = 4)
        var (peers, npcs) = BuildWorld(server, Core().HullTypes.Values.First(), peerCount: 1, npcCount);
        Assert.Equal(npcCount, npcs.Count);

        server.TestResetBroadcastState();
        server.TestSeedPeers(peers);
        server.TestSetShipUpdateModel(false);   // PORT mode — enemy poses ride HEAVY only
        var keys = server.TestCollectBroadcastKeys(0, parallel: false, step: "shipsUpdate");

        var peerConn = peers[0].Conn;
        Assert.True(keys.ContainsKey(peerConn), "the peer must receive its ships-update frame");
        var (heavy, light) = (0, 0);
        foreach (var (pkt, key, _) in keys[peerConn])
        {
            Assert.Equal(2, key);   // CoalesceShipsUpdate
            var (h, l) = CountShipsUpdate(pkt);
            heavy += h; light += l;
        }
        int expectedHeavy = Math.Max(2, (int)Math.Ceiling(npcCount / 30d));   // 30 = DeadReckonExtrapolationTicks
        Assert.True(heavy >= expectedHeavy,
            $"P632: at {npcCount} in-sector CROSS-SIDE enemies, PORT mode must serve ≥ {expectedHeavy} heavy/tick to keep enemy poses within the " +
            $"1.5 s dead-reckoner horizon (got {heavy}) — the fixed budget-2 freezes enemy poses → bots cannot fight (0 kills at 195v195)");
        Console.WriteLine($"P632-DENSITY-PORT: at {npcCount} in-sector enemies, peer gets {heavy} heavy + {light} light " +
                          $"/tick (⌈N/30⌉={expectedHeavy}) in port mode, enemy pose interval ≤ 1.5 s");
    }
}
