using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P758 — the real client's <c>FM_S_WARP_BOMB(188)</c> handler, the port of
/// native <c>clintlib/appmsg.cpp:850-868</c>:
/// <list type="bullet">
/// <item>gated on <c>IsInGame()</c> (:852) — a lobby client ignores it;</item>
/// <item>resolves the bombed warp from the world table and records the bomb
/// (<c>pwarp-&gt;AddBomb(ClientTimeFromServerTime(timeExplosion), pmt)</c>,
/// :857-866) — an unknown warp id is ignored (native's <c>if (pwarp)</c>);</item>
/// <item>when the LOCAL ship's sector is either end of the bombed aleph, the
/// critical console line <c>"Aleph to %s destabilized"</c> names the aleph's
/// OTHER sector (:867-868).</item>
/// </list>
/// The message is whole-mission news (the server broadcasts it to every side,
/// <c>FedSrv.CPP:12558</c>), so the state is recorded for every bombed aleph —
/// only the console line is sector-gated.
/// </summary>
public class P758WarpBombHandlerTests
{
    private static ServerShipUpdate Pose(short id) => ServerShipUpdate.FromValues(
        id, stateM: 0, controls: default, time: 1u,
        position: Vector3.Zero, orientation: Orientation.Identity, velocity: Vector3.Zero,
        turnRates: [0f, 0f, 0f],
        hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
        energyFrac: 1f, oreFrac: 0f, power: 0f);

    private static void SeedCluster(ClientWorld w, short id, string name) =>
        w.Apply(new ExportCluster
        {
            Cluster = new DataClusterIGC(
                1u, [0f, 0f, 1f], 0xffffffff, 0f, 0f, id, 0, 0,
                name, "", "", 0, 0, 0, true, false),
        });

    private static void SeedWarp(ClientWorld w, short id, short destId, short cluster) =>
        w.Apply(new ExportWarp
        {
            ObjectType = ExportWarp.OtWarp,
            Warp = new DataWarpIGC(id, destId, 60, "a", "a", "a",
                [0f, 0f, 0f], [0f, 0f, -1f], [0f, 0f, 0f, 1f], 0f, cluster),
        });

    /// <summary>Seed a world whose local ship (id <paramref name="me"/>) is
    /// seated in <paramref name="sector"/>, the mission has entered game, and
    /// aleph 400 (cluster 1) ↔ aleph 401 (cluster 2) are both known.</summary>
    private static ClientWorld SeatedBombWorld(short me, short sector, double nowSeconds = 42.0)
    {
        var w = new ClientWorld { NowSeconds = () => nowSeconds };
        w.Apply(new EnterGame());
        SeedCluster(w, 1, "Home");
        SeedCluster(w, 2, "Enemy");
        SeedWarp(w, 400, 401, cluster: 1);
        SeedWarp(w, 401, 400, cluster: 2);
        w.Apply(new SetCluster { SectorId = sector, Ship = Pose(me), Cookie = 1 });
        return w;
    }

    [Fact]
    public void Bombed_aleph_own_sector_names_the_far_end_and_records_state()
    {
        var w = SeatedBombWorld(me: 700, sector: 1, nowSeconds: 42.0);

        w.Apply(new WarpBomb { WarpIdBombed = 400, TimeExplosion = 123000u, ExpendableIdMissile = 950 });

        // State recorded for every bombed aleph (whole-mission news).
        var state = Assert.Single(w.WarpBombs.Values);
        Assert.Equal(123.0, state.ExplosionServerSeconds);   // ClientTimeFromServerTime, appmsg.cpp:861
        Assert.Equal((short)950, state.MissileTypeId);
        Assert.Equal(42.0, state.ReceivedSeconds);

        // Own ship is in the bombed aleph's own cluster (1): the line names the
        // FAR end — the partner aleph's cluster (2), "Enemy".
        var line = Assert.Single(w.Events.Chats);
        Assert.Equal("Aleph to Enemy destabilized", line.Message);
    }

    [Fact]
    public void Bombed_aleph_far_end_names_the_bombed_aleph_sector()
    {
        var w = SeatedBombWorld(me: 701, sector: 2);   // own ship sits at the destination end

        w.Apply(new WarpBomb { WarpIdBombed = 400, TimeExplosion = 200000u, ExpendableIdMissile = 951 });

        var line = Assert.Single(w.Events.Chats);
        Assert.Equal("Aleph to Home destabilized", line.Message);
        Assert.Equal(200.0, Assert.Single(w.WarpBombs.Values).ExplosionServerSeconds);
    }

    [Fact]
    public void Bombed_aleph_in_other_sector_records_state_but_no_alert_line()
    {
        var w = SeatedBombWorld(me: 702, sector: 3);   // neither end of aleph 400/401

        w.Apply(new WarpBomb { WarpIdBombed = 400, TimeExplosion = 300000u, ExpendableIdMissile = 952 });

        Assert.Single(w.WarpBombs);                   // whole-mission news is still recorded
        Assert.Empty(w.Events.Chats);                 // but the sector-gated line is not posted
    }

    [Fact]
    public void Unknown_bombed_warp_is_ignored()
    {
        var w = SeatedBombWorld(me: 703, sector: 1);

        w.Apply(new WarpBomb { WarpIdBombed = 999, TimeExplosion = 400000u, ExpendableIdMissile = 953 });

        Assert.Empty(w.WarpBombs);                    // native `if (pwarp)` (appmsg.cpp:857)
        Assert.Empty(w.Events.Chats);
    }

    [Fact]
    public void Lobby_client_ignores_warp_bomb()
    {
        var w = new ClientWorld { NowSeconds = () => 42.0 };
        SeedCluster(w, 1, "Home");
        SeedWarp(w, 400, 401, cluster: 1);
        // No EnterGame() and no own ship: IsInGame() is false (appmsg.cpp:852).

        w.Apply(new WarpBomb { WarpIdBombed = 400, TimeExplosion = 500000u, ExpendableIdMissile = 954 });

        Assert.Empty(w.WarpBombs);
        Assert.Empty(w.Events.Chats);
    }
}
