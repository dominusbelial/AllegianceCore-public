using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// P136 — deterministic, isolated codec round-trips for the two multi-ship
/// visibility messages (<c>LIGHT_SHIPS_UPDATE</c> 175 / <c>HEAVY_SHIPS_UPDATE</c>
/// 176). Predicate style, no live sim-loop wiring (foundation slice): each test
/// builds the message with real field values, encodes, asserts the exact wire
/// layout, decodes, and checks every field round-trips — including the heavy
/// payload's delta-compressed time/position recovering the source absolutes
/// through the message's <c>timeReference</c>/<c>positionReference</c>.
/// </summary>
public class LightHeavyShipsUpdateCodecTests
{
    /// <summary>⭐ <c>LIGHT_SHIPS_UPDATE</c>(175) with several
    /// <see cref="LightShipUpdateEntry"/>s plus an opaque inactive-turret blob
    /// survives Encode/Decode with the exact wire layout: fmid 175, two ib/cb
    /// var pairs, count implicit in the 8-byte element stride.</summary>
    [Fact]
    public void LightShipsUpdate_codec_round_trips_entries_and_turret_blob()
    {
        var msg = new LightShipsUpdate
        {
            // two ShipID shorts (1, 2), carried opaquely per the foundation slice
            InactiveTurretBlob = new byte[] { 0x01, 0x00, 0x02, 0x00 },
            LightShipEntries =
            [
                LightShipUpdateEntry.From((short)9001, 0x0005, new ControlData(0.5f, -0.25f, 1.0f, 0.0f)),
                LightShipUpdateEntry.From((short)9002, 0,        new ControlData(-1.0f, 0.5f, 0.0f, 0.75f)),
                LightShipUpdateEntry.From((short)9003, 0x0100,   new ControlData(0.0f, 0.0f, 0.25f, -0.5f)),
            ],
        };

        byte[] wire = msg.Encode();
        Assert.Equal(12 + 4 + 3 * 8, wire.Length);          // FixedSize 12 + blob(4) + 3 entries × 8
        Assert.Equal((ushort)175, BitConverter.ToUInt16(wire, 2));   // fmid
        Assert.Equal((ushort)12, BitConverter.ToUInt16(wire, 4));    // inactive ib = fixed-region end
        Assert.Equal((ushort)4, BitConverter.ToUInt16(wire, 6));     // inactive cb = 2 × ShipID
        Assert.Equal((ushort)16, BitConverter.ToUInt16(wire, 8));    // light ib = 12 + 4
        Assert.Equal((ushort)24, BitConverter.ToUInt16(wire, 10));   // light cb = 3 × 8
        Assert.Equal((ushort)wire.Length, BitConverter.ToUInt16(wire, 0));   // cbmsg

        var dec = LightShipsUpdate.Decode(wire);
        Assert.Equal(3, dec.LightShipEntries.Length);
        Assert.Equal((short)9001, dec.LightShipEntries[0].ShipId);
        Assert.Equal((ushort)0x0005, dec.LightShipEntries[0].StateM);
        Assert.Equal((short)9002, dec.LightShipEntries[1].ShipId);
        Assert.Equal((short)9003, dec.LightShipEntries[2].ShipId);
        Assert.Equal((ushort)0x0100, dec.LightShipEntries[2].StateM);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x02, 0x00 }, dec.InactiveTurretBlob);   // opaque passthrough intact
    }

    /// <summary>⭐ <c>HEAVY_SHIPS_UPDATE</c>(176) with two
    /// <see cref="HeavyShipUpdateEntry"/>s survives Encode/Decode exactly —
    /// exercising the CompactTime/CompactPosition <b>delta</b> path: the
    /// decoded absolutes match the source time/position (within the CompactPosition
    /// ±~0.5u accuracy at non-zero offsets; <b>exactly</b> for a zero offset),
    /// plus the scalar region and an opaque active-turret blob round-trip.</summary>
    [Fact]
    public void HeavyShipsUpdate_codec_round_trips_delta_payload()
    {
        uint timeRef = 501000u;
        var posRef = new Vector3(1000f, -500f, 700f);
        var msg = new HeavyShipsUpdate
        {
            TimeReference = timeRef,
            PositionReference = posRef,
            BpTargetHull = 180,
            BpTargetShield = 90,
            FractionsHull = 200, FractionsShield = 100, FractionsAmmo = 240,
            FractionsFuel = 200, FractionsEnergy = 180, FractionsOre = 50,
            ActiveTurretBlob = new byte[] { 0x05, 0x00, 0x06, 0x00, 0x07, 0x00, 0x08, 0x00 },
            HeavyShipEntries =
            [
                // non-zero time/position deltas, real kinematics
                HeavyShipUpdateEntry.From(timeRef, posRef,
                    (short)7101, 0x0002, new ControlData(0.5f, -0.25f, 1.0f, 0.0f),
                    501010u, new Vector3(1150f, -460f, 645f),
                    new Orientation(), new Vector3(30f, -10f, 5f), new[] { 0.5f, -0.2f, 0.1f },
                    0.875f, 0.625f, 0.3375f),
                // zero deltas: position == reference, time == reference
                HeavyShipUpdateEntry.From(timeRef, posRef,
                    (short)7102, 0, new ControlData(0f, 0f, 0f, 0f),
                    timeRef, posRef,
                    new Orientation(), new Vector3(0f, 0f, 0f), new[] { 0f, 0f, 0f },
                    0.5f, 0f, 0f),
            ],
        };

        byte[] wire = msg.Encode();
        Assert.Equal(36 + 8 + 2 * 39, wire.Length);         // FixedSize 36 + blob(8) + 2 entries × 39
        Assert.Equal((ushort)176, BitConverter.ToUInt16(wire, 2));   // fmid
        Assert.Equal((ushort)36, BitConverter.ToUInt16(wire, 4));    // active ib = fixed-region end
        Assert.Equal((ushort)8, BitConverter.ToUInt16(wire, 6));     // active cb
        Assert.Equal((ushort)44, BitConverter.ToUInt16(wire, 8));    // heavy ib = 36 + 8
        Assert.Equal((ushort)78, BitConverter.ToUInt16(wire, 10));   // heavy cb = 2 × 39
        Assert.Equal(timeRef, BitConverter.ToUInt32(wire, 12));        // timeReference scalar
        Assert.Equal(posRef.X, BitConverter.ToSingle(wire, 16), 1e-4f);
        Assert.Equal(posRef.Y, BitConverter.ToSingle(wire, 20), 1e-4f);
        Assert.Equal(posRef.Z, BitConverter.ToSingle(wire, 24), 1e-4f);
        Assert.Equal((ushort)wire.Length, BitConverter.ToUInt16(wire, 0));   // cbmsg

        var dec = HeavyShipsUpdate.Decode(wire);
        Assert.Equal(timeRef, dec.TimeReference);
        Assert.Equal(posRef, dec.PositionReference);
        Assert.Equal((byte)180, dec.BpTargetHull);
        Assert.Equal((byte)90, dec.BpTargetShield);
        Assert.Equal(new byte[] { 200, 100, 240, 200, 180, 50 },
            new[] { dec.FractionsHull, dec.FractionsShield, dec.FractionsAmmo, dec.FractionsFuel, dec.FractionsEnergy, dec.FractionsOre });
        Assert.Equal(new byte[] { 0x05, 0x00, 0x06, 0x00, 0x07, 0x00, 0x08, 0x00 }, dec.ActiveTurretBlob);

        Assert.Equal(2, dec.HeavyShipEntries.Length);

        var e0 = dec.HeavyShipEntries[0];
        Assert.Equal((short)7101, e0.ShipId);
        Assert.Equal(0x0002, e0.StateM);
        Assert.Equal(501010u, e0.Time(dec.TimeReference));                       // CompactTime delta exact
        Assert.Equal(1150f, e0.Position(dec.PositionReference).X, 1.0f);         // CompactPosition ±~0.5u
        Assert.Equal(-460f, e0.Position(dec.PositionReference).Y, 1.0f);
        Assert.Equal(645f, e0.Position(dec.PositionReference).Z, 1.0f);
        Assert.Equal(0.875f, e0.HullFraction);    // Percent(0.875)=210 → 210/240, exact
        Assert.Equal(0.624f, e0.ShieldFraction, 1e-3f);  // Percent(0.625)=150 → 150/240 = 0.625
        Assert.Equal(0.338f, e0.OreFraction, 1e-3f);     // Percent(0.3375)=81 → 81/240 = 0.3375
        Assert.Equal(30f, e0.Velocity.X, 1f);                                    // CompactVelocity 5-byte
        Assert.Equal(0.492f, e0.TurnRates[0], 1e-2f);                            // CompactTurnRates

        var e1 = dec.HeavyShipEntries[1];
        Assert.Equal((short)7102, e1.ShipId);
        Assert.Equal(timeRef, e1.Time(dec.TimeReference));
        Assert.Equal(posRef, e1.Position(dec.PositionReference));               // zero delta ⇒ exact
    }
}
