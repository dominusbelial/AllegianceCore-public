using Allegiance.Core.Sim;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>OWN_SHIP_MINING_UPDATE</c> (id 206) — the port's <b>owner-only</b>,
/// per-tick extension message carrying <em>which</em> asteroid the local
/// player's own ship is currently mining. It is the renderer-only client's
/// answer to a gap in the native wire: native exposes mining via the
/// <c>miningMaskIGC</c> state bit (igc.h:822) riding the compact ship status,
/// but it never transmits WHICH rock a mining ship is working — an observer
/// (and the own client) infers that locally from the sim (native
/// <c>shipIGC.cpp:2206-2256</c>). This renderer-only client has no static
/// sim/core to do that inference from, so the server streams the exact rock
/// id, following the exact owner-only per-tick precedent P230
/// (<c>MissileLockUpdate</c> 204) and P234 (<c>MissileUpdate</c> 205) set for
/// state native's client derived locally but this client cannot.
/// <para>
/// The <b>mining on/off</b> signal proper rides the faithful native bit —
/// <c>miningMaskIGC</c> (<see cref="Sim.ShipControlState.MiningMask"/>, value
/// 0x2000) — stamped into the own ship's <c>ServerShipUpdate</c> stateM on the
/// existing <c>SingleShipUpdate(78)</c> broadcast (see
/// <see cref="Server.Server.BroadcastShipStates"/>). This message complements
/// it by identifying the target rock (and, defensively, its sector). When not
/// mining both ids are NA (-1).
/// </para>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 206
///   offset 4: short  clusterID      (SectorID — the rock's sector; NA -1 when not mining)
///   offset 6: short  asteroidID     (AsteroidID — the rock being drilled; NA -1 when not mining)
///   = 8 B fixed, no var items → cbmsg = 8
/// </code>
/// Same short/short fixed shape as <see cref="AsteroidMined"/> (198), distinct
/// in fmid and in naming the DRILLING SHIP's target for the ship's owner
/// rather than reporting a rock's ore fraction to the sector.
/// </summary>
public sealed class OwnShipMiningUpdate : IFedMessage
{
    public const ushort Fmid = 206;

    /// <summary>Wire width of the whole message: header(4) + clusterID(2) +
    /// asteroidID(2). No alignment gaps.</summary>
    public const int FixedSize = 8;

    /// <summary>The mining asteroid's sector id; NA (-1) when not mining.</summary>
    public short ClusterId;

    /// <summary>The mining asteroid's id; NA (-1) when not mining.</summary>
    public short AsteroidId;

    /// <summary>True iff a rock is currently being mined (ids != NA).</summary>
    public bool IsMining => AsteroidId != -1;

    public ushort GetFmid() => Fmid;

    /// <summary>
    /// Build the owner-only mining update from the rock the ship is currently
    /// drilling (<paramref name="rock"/>, the per-tick <c>Ship.MiningAsteroid</c>)
    /// — or a not-mining (NA/NA) message when <c>null</c>.
    /// </summary>
    public static OwnShipMiningUpdate FromMining(Asteroid? rock)
    {
        if (rock is null) return new OwnShipMiningUpdate { ClusterId = -1, AsteroidId = -1 };
        return new OwnShipMiningUpdate
        {
            ClusterId = rock.Data.ClusterId,
            AsteroidId = rock.Data.AsteroidId,
        };
    }

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ClusterId);
        w.WriteI16(AsteroidId);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"OWN_SHIP_MINING_UPDATE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static OwnShipMiningUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"OWN_SHIP_MINING_UPDATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected OWN_SHIP_MINING_UPDATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new OwnShipMiningUpdate
        {
            ClusterId = r.ReadI16(),
            AsteroidId = r.ReadI16(),
        };
    }
}
