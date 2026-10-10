using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P551 — the dock ore-sale GATE. Native sells carried ore on dock only when
/// the docked station's type carries <c>c_sabmUnload</c> AND it is the ship's
/// OWN side (IShipIGC::SetStation, shipIGC.h:441-449: <c>if ((m_fOre &gt; 0.0f)
/// &amp;&amp; (s->GetStationType()->HasCapability(c_sabmUnload) &amp;&amp;
/// (GetSide() == s->GetSide())))</c>). The port's dock sale previously sold
/// unconditionally; P551 adds the gate. Three direct-event proofs:
///   (1) an own-side SabmUnload station sells the ore (side treasury credited,
///       ore zeroed) — the P528b miner loop still works;
///   (2) an own-side NON-SabmUnload station sells NOTHING (ore kept, treasury
///       unchanged);
///   (3) an ENEMY-side SabmUnload station sells NOTHING (ore kept).
/// Uses <see cref="ServerHost.DockWithStationEvent"/> directly (the same seam
/// the sector dock pass raises) with a bare carrier ship, so the side has no
/// seated peers and OnPayday's zero-player branch keeps the proceeds in the
/// treasury — making the credit exact and deterministic.
/// </summary>
[Collection("LiveNetwork")]
public class P551DockOreSaleTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private const short FlySector = 1052;   // XXV_simple, no-station fly sector (P111 measurement)

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
        Games = [new Gm { Name = "P551Dock", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    private static Ship PlaceCarrier(Srv server, short id, short sideId, float ore)
    {
        var mission = server.Missions[0];
        var fly = mission.Sectors.First(s => s.Id == FlySector);
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var ship = new Ship(id, hull, new Vector3(500f, 500f, 500f), Vector3.Zero, Orientation.Identity)
        {
            SideId = sideId,
            Ore = ore,
        };
        mission.PlaceShip(ship, fly);
        return ship;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Dock_ore_sale_honours_sabmunload_and_own_side_gate()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        var mission = server.Missions[0];
        var side = mission.Sides.First(s => s.Id == 0);
        var baseStation = mission.Sectors.SelectMany(s => s.Stations).First();

        // A station type that CAN unload and one that CANNOT (probed dynamically).
        short unloadType = mission.Core.StationTypes.Values.First(st => st.HasCapability(DataStationTypeIGC.SabmUnload)).StationTypeId;
        short nonUnloadType = mission.Core.StationTypes.Values.First(st => !st.HasCapability(DataStationTypeIGC.SabmUnload)).StationTypeId;

        // ── (1) own-side SabmUnload station: ore SOLD ─────────────────────
        var carrier1 = PlaceCarrier(server, 2101, 0, ore: 100f);
        var ownUnload = new DockStation(baseStation with { StationTypeId = unloadType, SideId = 0 });
        long before1 = side.Money;
        server.Host.DockWithStationEvent(carrier1, ownUnload);
        Assert.Equal(0f, carrier1.Ore);                                   // ore zeroed
        Assert.True(side.Money > before1, $"ore sale should credit the side treasury ({before1} -> {side.Money})");
        float credited1 = side.Money - before1;
        Console.WriteLine($"[DOCKSELL-P551] own Unload station: carrier {carrier1.Id} sold 100 ore -> ore zeroed, side +{credited1:F1} credits");

        // ── (2) own-side NON-SabmUnload station: ore KEPT ─────────────────
        var carrier2 = PlaceCarrier(server, 2102, 0, ore: 100f);
        var ownNonUnload = new DockStation(baseStation with { StationTypeId = nonUnloadType, SideId = 0 });
        long before2 = side.Money;
        server.Host.DockWithStationEvent(carrier2, ownNonUnload);
        Assert.Equal(100f, carrier2.Ore);                                 // NOT sold — ore kept
        Assert.Equal(before2, side.Money);                                // treasury untouched
        Console.WriteLine($"[DOCKSELL-P551] own NON-Unload station: carrier {carrier2.Id} kept 100 ore, treasury unchanged ({side.Money})");

        // ── (3) ENEMY-side SabmUnload station: ore KEPT ───────────────────
        var carrier3 = PlaceCarrier(server, 2103, 0, ore: 100f);
        var enemyUnload = new DockStation(baseStation with { StationTypeId = unloadType, SideId = 1 });   // enemy side
        long before3 = side.Money;
        server.Host.DockWithStationEvent(carrier3, enemyUnload);
        Assert.Equal(100f, carrier3.Ore);                                 // NOT sold — own-side gate
        Assert.Equal(before3, side.Money);                                // treasury untouched
        Console.WriteLine($"[DOCKSELL-P551] ENEMY Unload station: carrier {carrier3.Id} kept 100 ore, treasury unchanged ({side.Money})");
    }
}
