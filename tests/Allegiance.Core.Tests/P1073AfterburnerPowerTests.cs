using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P1073 — native exports the mounted afterburner's live power ramp in the ship
/// update (<c>pshipupdate-&gt;power = p ? BytePercentage(p-&gt;GetPower()) : 0</c>,
/// <c>Igc/shipIGC.h:1137</c>). The port used to hardcode the slot to 1.0
/// (<c>ServerShipUpdate</c> byte 46 / <c>HeavyShipUpdateEntry</c> byte 38), so the
/// HUD's PWR readout could not follow the native quantity. This pins the server
/// simulation → wire value, and the HUD's local-simulation source.
/// </summary>
public sealed class P1073AfterburnerPowerTests
{
    private static readonly Lazy<StaticCore> Core = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            try
            {
                var file = IgcReader.ParseFile(path);
                if (file.Wrapper == IgcWrapper.VersionAndDataSize) return StaticCore.Load(file);
            }
            catch { }
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static Ship NewShip()
    {
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First() with
        { Speed = 100f, Thrust = 1000f, MaxFuel = 1000f };
        return new Ship(shipId: 41, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
    }

    private static DataPartTypeIGC AfterburnerType(float onRate, float offRate) => new(
        Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ab", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
        PartId: 9300, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
        Type: new DataPartTypeIGC.Afterburner(FuelConsumption: 0.0001f, MaxThrust: 100f,
            OnRate: onRate, OffRate: offRate, InteriorSound: 0, ExteriorSound: 0));

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Server_ship_update_carries_the_live_afterburner_ramp()
    {
        Require.Artwork();
        var ship = NewShip();
        ship.MountPart(AfterburnerType(onRate: 1e6f, offRate: 1e6f), 1, 0, 255);
        ship.StateM |= ShipControlState.AfterburnerButton;

        Assert.Equal(0f, ship.AfterburnerPower);
        ship.Update(0.05);
        Assert.Equal(1f, ship.AfterburnerPower, 3);
        Assert.Equal(1f, ServerShipUpdate.FromModel(ship.Id, ship, 7).Power, 3);
        Assert.Equal(1f, HeavyShipUpdateEntry.From(7, Vector3.Zero, ship, 7).Power, 3);

        ship.StateM &= ~ShipControlState.AfterburnerButton;
        ship.Update(0.10);
        Assert.Equal(0f, ship.AfterburnerPower, 3);
        Assert.Equal(0f, ServerShipUpdate.FromModel(ship.Id, ship, 8).Power, 3);
    }
}
