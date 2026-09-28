using Allegiance.Core.Wire;
using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P414 — pure unit tests for the ripcord request key: (1) the outbound wire
/// message encodes the selected starmap sector id exactly (Encode → Decode is
/// byte-faithful, so the sector the pilot picks is the sector the server
/// receives), and (2) the pure key-decision rule (<see cref="RipcordKey"/>)
/// that turns an active-ripcord / selected-sector context into the right
/// <c>sidRipcord</c> to send.
/// </summary>
public sealed class RipcordRequestTests
{
    // ── outbound encode ────────────────────────────────────────────────────

    [Fact]
    public void Outbound_request_encodes_the_selected_sector_id_exactly()
    {
        short selected = 1054;   // Deucalion in the live-test fixture
        var encoded = new RipcordRequest { SidRipcord = selected }.Encode();

        var decoded = RipcordRequest.Decode(encoded);
        Assert.Equal(RipcordRequest.Fmid, decoded.GetFmid());
        // The sector the pilot picks on the starmap is the sector the server's
        // RequestRipcordCore receives — the whole point of the outbound encode.
        Assert.Equal(selected, decoded.SidRipcord);
    }

    [Fact]
    public void Outbound_cancel_request_encodes_NA()
    {
        var encoded = new RipcordRequest { SidRipcord = RipcordRequest.NA }.Encode();

        var decoded = RipcordRequest.Decode(encoded);
        Assert.Equal(RipcordRequest.Fmid, decoded.GetFmid());
        Assert.Equal(RipcordRequest.NA, decoded.SidRipcord);
    }

    [Fact]
    public void Decode_rejects_a_non_ripcord_request_fmid()
    {
        // A defensive check that Decode is keyed to its own fmid and does not
        // silently accept a foreign message.
        var bogus = new RipcordActivate
        {
            ShipidRipcord = 1, OtRipcord = 0, OidRipcord = 1, SidRipcord = 1054,
        }.Encode();
        Assert.Throws<ArgumentException>(() => RipcordRequest.Decode(bogus));
    }

    // ── pure key-decision rule ─────────────────────────────────────────────

    [Fact]
    public void Active_ripcord_key_press_cancels_with_NA()
    {
        // Pressing the ripcord key while our own countdown is active cancels it
        // (native treats cancel as the same command; this port's key doubles as
        // the cancel key) — regardless of any starmap selection still held.
        short? target = RipcordKey.RequestFor(ripcordActive: true, selectedSectorId: 1054);
        Assert.Equal(RipcordRequest.NA, target);
    }

    [Fact]
    public void No_active_ripcord_with_selection_starts_it_toward_that_sector()
    {
        // No countdown + a starmap sector selected → initiate toward that sector.
        short? target = RipcordKey.RequestFor(ripcordActive: false, selectedSectorId: 1054);
        Assert.Equal((short)1054, target);
    }

    [Fact]
    public void No_active_ripcord_and_no_selection_sends_nothing()
    {
        // No countdown to cancel and no destination picked → no wire message
        // (the caller surfaces a "select a sector first" hint instead). Sending
        // NA here would be a benign server no-op, so signalling is the only gain.
        short? target = RipcordKey.RequestFor(ripcordActive: false, selectedSectorId: null);
        Assert.Null(target);
    }
}
