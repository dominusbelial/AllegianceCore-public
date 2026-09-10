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
    /// <see cref="LightShipUpdateEntry"/>s plus a typed inactive-turret ShipID
    /// list survives Encode/Decode with the exact wire layout: fmid 175, two ib/cb
    /// var pairs, count implicit in the 8-byte element stride.</summary>
    [Fact]
    public void LightShipsUpdate_codec_round_trips_entries_and_inactive_turrets()
    {
        var msg = new LightShipsUpdate
        {
            // two ShipID shorts (1, 2) — typed relay list, no per-turret state
            InactiveTurretShipIds = new short[] { 1, 2 },
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
        Assert.Equal(new short[] { 1, 2 }, dec.InactiveTurretShipIds);   // typed relay list intact
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
            ActiveTurretUpdates =
            [
                // one typed ServerActiveTurretUpdate (16 B): shipID + CompactTime
                // delta (501030−501000=30) + controls + orientation
                ServerActiveTurretUpdate.From((short)7390, timeRef, 501030u,
                    new ControlData(0.5f, -0.25f, 1.0f, 0.0f), new Orientation()),
            ],
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
        Assert.Equal(36 + 16 + 2 * 39, wire.Length);        // FixedSize 36 + 1×16 turret + 2 entries × 39
        Assert.Equal((ushort)176, BitConverter.ToUInt16(wire, 2));   // fmid
        Assert.Equal((ushort)36, BitConverter.ToUInt16(wire, 4));    // active ib = fixed-region end
        Assert.Equal((ushort)16, BitConverter.ToUInt16(wire, 6));    // active cb = 1 × ServerActiveTurretUpdate(16)
        Assert.Equal((ushort)52, BitConverter.ToUInt16(wire, 8));    // heavy ib = 36 + 16
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
        Assert.Single(dec.ActiveTurretUpdates);                                  // typed relay record
        Assert.Equal((short)7390, dec.ActiveTurretUpdates[0].ShipId);
        Assert.Equal(501030u, dec.ActiveTurretUpdates[0].Time(dec.TimeReference)); // CompactTime delta exact

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

    /// <summary>⭐ <c>ServerActiveTurretUpdate</c> — the heavy message's typed
    /// turret-relay record (<c>igc.h:2478-2485</c>) — encodes to exactly 16
    /// bytes (shipID + CompactTime + CompactControlData + CompactOrientation,
    /// pack(1)) and round-trips, with the CompactTime delta recovering the
    /// source absolute through the message's timeReference. Distinguishes the
    /// S→C relay shape (has a ShipID, short time) from P109's
    /// <c>ClientActiveTurretUpdate</c>.</summary>
    [Fact]
    public void ServerActiveTurretUpdate_exact_layout_and_round_trip()
    {
        uint timeRef = 640000u;
        var upd = ServerActiveTurretUpdate.From(
            (short)8842, timeRef, 640075u,                          // delta +75
            new ControlData(0.5f, -0.25f, 1.0f, 0.0f),
            new Orientation());

        Assert.Equal(16, ServerActiveTurretUpdate.Width);

        // exact byte layout against igc.h:2478-2485 (pack(1))
        byte[] b = new byte[16];
        WireWriter w = new(b);
        upd.EncodeBody(ref w);
        Assert.Equal((short)8842, BitConverter.ToInt16(b, 0));      // shipID @0
        Assert.Equal((short)-75, BitConverter.ToInt16(b, 2));       // CompactTime @2 = timeRef − time (−75)
        // controls @4..7, orientation @8..15 — verified via round-trip accessors

        WireReader r = new(b);
        var dec = ServerActiveTurretUpdate.DecodeBody(ref r);
        Assert.Equal((short)8842, dec.ShipId);
        Assert.Equal((short)-75, dec.TimeOffset);
        Assert.Equal(640075u, dec.Time(timeRef));                   // absolute recovered exactly
        Assert.Equal(0.5039370f, dec.Controls.Yaw, 1e-3f);          // ControlAxis(0.5)=64 → 64/127
        Assert.Equal(-0.251969f, dec.Controls.Pitch, 1e-3f);        // ControlAxis(-0.25)=floor(-31.25)=-32 → -32/127
        Assert.Equal(1.0f, dec.Controls.Roll, 1e-3f);
        Assert.Equal(0.0f, dec.Controls.Throttle, 1e-3f);           // ControlAxis(0.0)=0
        Assert.Equal(new Orientation().GetForward(), dec.Orientation.GetForward());  // identity orientation round-trips
    }

    /// <summary>⭐ <c>HEAVY_SHIPS_UPDATE</c> accepts a non-empty typed
    /// active-turret relay list via <see cref="HeavyShipsUpdate.FromShips"/>
    /// and the round-trip preserves every record, proving the builder path and
    /// the codec agree on the typed (non-blob) shape.</summary>
    [Fact]
    public void HeavyShipsUpdate_FromShips_with_typed_active_turrets_round_trips()
    {
        uint timeRef = 901000u;
        var posRef = new Vector3(0f, 0f, 0f);
        var turrets = new[]
        {
            ServerActiveTurretUpdate.From((short)501, timeRef, 901005u, new ControlData(0f, 0f, 0f, 0f), new Orientation()),
            ServerActiveTurretUpdate.From((short)502, timeRef, 901012u, new ControlData(1f, -1f, 0.5f, 0.25f), new Orientation()),
        };
        var msg = HeavyShipsUpdate.FromShips(timeRef, posRef, Enumerable.Empty<Ship>(), turrets);

        var dec = HeavyShipsUpdate.Decode(msg.Encode());
        Assert.Equal(2, dec.ActiveTurretUpdates.Length);
        Assert.Equal((short)501, dec.ActiveTurretUpdates[0].ShipId);
        Assert.Equal(901005u, dec.ActiveTurretUpdates[0].Time(dec.TimeReference));
        Assert.Equal((short)502, dec.ActiveTurretUpdates[1].ShipId);
        Assert.Equal(901012u, dec.ActiveTurretUpdates[1].Time(dec.TimeReference));
        Assert.Equal(1.0f, dec.ActiveTurretUpdates[1].Controls.Yaw, 1e-2f);    // ControlAxis(1.0)=127 → 127/127=1
        Assert.Equal(0.5039f, dec.ActiveTurretUpdates[1].Controls.Roll, 1e-2f); // ControlAxis(0.5)=64 → 64/127
    }

    /// <summary>⭐ <c>LIGHT_SHIPS_UPDATE</c> accepts a typed inactive-turret
    /// ShipID list via <see cref="LightShipsUpdate.FromShips"/> and round-trips
    /// it, confirming the builder packs the short[] identically to the codec.</summary>
    [Fact]
    public void LightShipsUpdate_FromShips_with_inactive_turrets_round_trips()
    {
        var msg = LightShipsUpdate.FromShips(Enumerable.Empty<Ship>(), new short[] { 777, -2 });

        var dec = LightShipsUpdate.Decode(msg.Encode());
        Assert.Equal(new short[] { 777, -2 }, dec.InactiveTurretShipIds);
        Assert.Empty(dec.LightShipEntries);
    }
}
