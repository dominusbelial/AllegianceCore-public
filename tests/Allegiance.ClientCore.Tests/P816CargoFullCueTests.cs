using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P816/SIM-0267 — the pickup leg of the cargo-full cue. Native's client
/// <c>HitTreasure(c_tcPart)</c> (shipIGC.h:1239-1305, called from the
/// ACQUIRE_TREASURE handler, clintlib/appmsg.cpp:270-275) answers a part offer
/// that fits nowhere with <c>salCargoFullSound</c> and <c>c_mountNA</c>
/// (shipIGC.h:1302-1304) — and plays <c>pickUpPartSound</c> only on the
/// SUCCESS path (shipIGC.h:1307). The port mirrors that from the session's
/// mount picker: a searched-and-full decline records
/// <see cref="ClientSoundEventKind.CargoFull"/> (wave
/// <c>vo_sal_cargofull</c>, sounddef.mdl:810) via
/// <see cref="ClientWorld.RecordPartOfferCargoFull"/> and suppresses the
/// P735 <see cref="ClientSoundEventKind.PickupPart"/> cue for that same offer.
/// The part-swap leg is the P719 in-flight refusal (WinTrek.cpp:9114), already
/// wired in Game1.FlightParts; the picker's full-ship decline itself is the
/// P758 <c>TreasureMountPickerTests.No_mount_and_full_cargo_declines_with_MountNA</c>.
/// </summary>
public class P816CargoFullCueTests
{
    private const short MyId = 816;

    private static ClientWorld SeatedWorld()
    {
        var w = new ClientWorld();
        w.Apply(new EnterGame());
        w.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromValues(
                MyId, stateM: 0, controls: default, time: 1u,
                position: Vector3.Zero, orientation: Orientation.Identity, velocity: Vector3.Zero,
                turnRates: [0f, 0f, 0f],
                hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
                energyFrac: 1f, oreFrac: 0f, power: 0f),
            Cookie = 1,
        });
        return w;
    }

    private static AcquireTreasure PartOffer(short treasureId = 500) => new()
    { TreasureCode = (byte)TreasureCode.Part, TreasureId = treasureId, Amount = 3 };

    [Fact]
    public void Declined_part_offer_records_cargo_full_instead_of_pickup_cue()
    {
        var w = SeatedWorld();

        // The session's mount picker declined the offer with the native
        // cargo-full condition (PickMount → MountNA on a searched ship).
        w.RecordPartOfferCargoFull();
        w.Apply(PartOffer());

        var events = w.TakeSoundEvents().ToList();
        Assert.Contains(events, e => e.Kind == ClientSoundEventKind.CargoFull && e.Id == MyId);
        Assert.DoesNotContain(events, e => e.Kind == ClientSoundEventKind.PickupPart);
    }

    [Fact]
    public void Accepted_part_offer_still_records_the_pickup_cue()
    {
        var w = SeatedWorld();

        w.Apply(PartOffer());

        var events = w.TakeSoundEvents().ToList();
        Assert.Contains(events, e => e.Kind == ClientSoundEventKind.PickupPart);
        Assert.DoesNotContain(events, e => e.Kind == ClientSoundEventKind.CargoFull);
    }

    [Fact]
    public void Decline_flag_is_consumed_immediately_and_cannot_leak_to_the_next_offer()
    {
        var w = SeatedWorld();

        // A cargo-full flag followed by a NON-part code must be consumed by
        // that apply (the flag is one-shot, cleared regardless of the code).
        w.RecordPartOfferCargoFull();
        w.Apply(new AcquireTreasure { TreasureCode = (byte)TreasureCode.Cash, TreasureId = 900, Amount = 900 });

        // The NEXT part offer is a normal accepted pickup — the flag must not
        // suppress its PickupPart cue.
        w.Apply(PartOffer());

        var events = w.TakeSoundEvents().ToList();
        Assert.Contains(events, e => e.Kind == ClientSoundEventKind.PickupPart);
        Assert.Contains(events, e => e.Kind == ClientSoundEventKind.CargoFull && e.Id == MyId);
    }
}
