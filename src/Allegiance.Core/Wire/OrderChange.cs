namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_ORDER_CHANGE</c> (id 93, <c>Inc/Messages.h:648-654</c>, quoted in
/// full): a player issuing a command to a ship (typically a drone/NPC or their
/// own ship) — the drone-AI command-tasking message whose handler
/// (<c>FedSrv.CPP:7264-7335</c>) branches on pilot type, dock state, and
/// <c>commandID</c>. P148 (Stage 2) wires only the <b>stay-docked</b> slice of
/// that handler.
/// <code>
///   DEFINE_FEDMSG(CS, ORDER_CHANGE, 93)
///     ShipID        shipID;
///     ObjectID      objectID;
///     ObjectType    objectType;
///     CommandID     commandID;
///     Command       command;
///   END_FEDMSG
/// </code>
/// </summary>
/// <remarks>
/// <para><b><c>command</c> vs <c>commandID</c> — do NOT conflate them.</b>
/// <c>command</c> is a <b>slot selector</b> naming which of the native 4-slot
/// command array (<c>c_cmdQueued=0</c>/<c>c_cmdAccepted=1</c>/<c>c_cmdCurrent=2</c>/
/// <c>c_cmdPlan=3</c>, <c>Igc/igc.h:848-852</c>, mirrored in
/// <c>Wopr/Wopr/Constants/CommandType.cs:9-15</c>) the command lands in.
/// <c>commandID</c> is the <b>actual action</b> (<c>c_cidDoNothing=-3</c> …
/// <c>c_cidBuild=9</c>, <c>Igc/igc.h:897-912</c>, mirrored in
/// <c>Wopr/Wopr/Constants/CommandID.cs:9-26</c>). They are different scalar
/// types (<c>Command</c> vs <c>CommandID</c>) with different value spaces; the
/// stay-docked gate reads only <c>commandID == c_cidDoNothing</c>
/// (<c>FedSrv.CPP:7290</c>), while the (out-of-scope) generic fallback reads
/// <c>command</c> as the array index (<c>FedSrv.CPP:7338</c>).</para>
/// <para><b>Wire widths (real finding, chased through the typedefs — both are
/// 1 byte, NOT short).</b> <c>Command</c> is <c>typedef char Command</c>
/// (<c>Igc/igc.h:842</c>) and <c>CommandID</c> is <c>typedef char CommandID</c>
/// (<c>Igc/igc.h:897</c>), both single signed bytes on the wire (MSVC <c>char</c>
/// is signed, so <c>-3</c> fits). The task brief proposed <c>short</c> as a
/// "reasonable default"; chasing the typedef chains (the same technique that
/// resolved <c>WingID</c>/<c>ObjectID</c> in P146) yields the real answer:
/// 1 byte each. By contrast <c>ShipID</c>/<c>ObjectID</c> are
/// <c>typedef short ObjectID</c> (<c>Igc/igc.h:477,480</c>) and <c>ObjectType</c>
/// is <c>typedef short ObjectType</c> (<c>Igc/igc.h:115</c>) — 2 bytes each.
/// Layout (C++ struct align 2):</para>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 93
///   offset 4: short  shipID       (ShipID)
///   offset 6: short  objectID     (ObjectID)
///   offset 8: short  objectType   (ObjectType)
///   offset 10: sbyte commandID    (CommandID, 1 byte)
///   offset 11: sbyte command      (Command, 1 byte)
///   = 12 B fixed, no var items → cbmsg = 12 (offset 12 needs no pad)
/// </code>
/// </remarks>
public sealed class OrderChange : IFedMessage
{
    public const ushort Fmid = 93;
    public const int FixedSize = 12;   // header + 3 shorts @4/6/8 + 2 singles @10/11

    /// <summary>Native <c>c_cidDoNothing = -3</c> (<c>igc.h:898</c>) — the
    /// "stay docked" command the P148 slice acts on.</summary>
    public const short CidDoNothing = -3;

    /// <summary>Native <c>c_cidNone = -1</c> (<c>igc.h:899</c>,
    /// <c>Wopr/Wopr/Constants/CommandID.cs:13</c>) — the "no command id"
    /// sentinel the return-to-base branch writes when clearing a slot
    /// (<c>SetCommand(c_cmdQueued, NULL, c_cidNone)</c>, <c>FedSrv.CPP:7322</c>).</summary>
    public const short CidNone = -1;

    /// <summary>Native <c>c_cidGoto = 5</c> (<c>igc.h:903</c>,
    /// <c>Wopr/Wopr/Constants/CommandID.cs:19</c>) — the "go to" action the
    /// take-cover branch stores in the Plan slot
    /// (<c>SetCommand(c_cmdPlan, pstationToHide, c_cidGoto)</c>,
    /// <c>shipIGC.h:2240</c>). P151 (the Plan slot) needs it the way P148/P150
    /// needed <see cref="CidDoNothing"/>/<see cref="CidNone"/>.</summary>
    public const short CidGoto = 5;

    /// <summary>Native command-slot selector <c>c_cmdQueued = 0</c>
    /// (<c>Igc/igc.h:848</c>, <c>Wopr/Wopr/Constants/CommandType.cs:11</c>) —
    /// the slot <see cref="Command"/> names. Server/AI-internal; never sent by
    /// the real client's three ORDER_CHANGE sites (WinTrek.cpp:3774/8838/8903).</summary>
    public const short CmdQueued = 0;

    /// <summary>Native command-slot selector <c>c_cmdAccepted = 1</c>
    /// (<c>Igc/igc.h:849</c>, <c>Wopr/Wopr/Constants/CommandType.cs:12</c>) —
    /// what the real client's two UI sends for the "return to base" action
    /// actually set (WinTrek.cpp:3778, 8907).</summary>
    public const short CmdAccepted = 1;

    /// <summary>Native command-slot selector <c>c_cmdCurrent = 2</c>
    /// (<c>Igc/igc.h:850</c>, <c>Wopr/Wopr/Constants/CommandType.cs:13</c>) —
    /// sent by one client send site (WinTrek.cpp:8842), a client-local
    /// autopilot setter; the port's corresponding slot is the weapon-aim
    /// <see cref="Ship.CommandTarget"/>.</summary>
    public const short CmdCurrent = 2;

    /// <summary>The id of the ship being commanded. The handler resolves it on
    /// the commanding player's own side (<c>FedSrv.CPP:7275-7278</c>); an
    /// unresolvable id leaves the target as the sender's own ship.</summary>
    public short ShipID;

    /// <summary>The id of the target model the generic fallback would command
    /// (e.g. a station/asteroid to mine/goto). Not read by the P148 stay-docked
    /// slice; used only by the out-of-scope <c>SetCommand</c> fallback
    /// (<c>FedSrv.CPP:7337</c>).</summary>
    public short ObjectID;

    /// <summary>The <see cref="IgcObjectType"/> of <see cref="ObjectID"/>
    /// (<c>typedef short ObjectType</c>, <c>igc.h:115</c>). Carried on the wire
    /// so the receiver knows what kind of object <c>objectID</c> names.</summary>
    public IgcObjectType ObjectType;

    /// <summary>The <em>action</em> command (<c>CommandID</c>, 1-byte signed),
    /// e.g. <see cref="CidDoNothing"/>=-3. Distinct from <see cref="Command"/>.
    /// The stay-docked gate tests <c>commandID == c_cidDoNothing</c>
    /// (<c>FedSrv.CPP:7290</c>).</summary>
    public short CommandId;

    /// <summary>The 4-slot command-array <em>index</em> (<c>Command</c>,
    /// 1-byte signed), <c>c_cmdQueued=0</c>..<c>c_cmdPlan=3</c> — which command
    /// slot the action targets. Not read by the P148 stay-docked slice; used by
    /// the out-of-scope generic fallback (<c>FedSrv.CPP:7338</c>). Distinct from
    /// <see cref="CommandId"/>.</summary>
    public short Command;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                       // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipID);                  // offset 4: shipID (ShipID short)
        w.WriteI16(ObjectID);                // offset 6: objectID (ObjectID short)
        w.WriteI16((short)ObjectType);       // offset 8: objectType (ObjectType short)
        w.WriteByte(unchecked((byte)(sbyte)CommandId));   // offset 10: commandID (CommandID char)
        w.WriteByte(unchecked((byte)(sbyte)Command));     // offset 11: command (Command char)
        if (w.Position != FixedSize) throw new InvalidOperationException($"ORDER_CHANGE internal layout drift: {w.Position} != {FixedSize}");
        w.WriteU16At(0, (ushort)w.Position);
        return buf;
    }

    public static OrderChange Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"ORDER_CHANGE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected ORDER_CHANGE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                // header
        short shipID = r.ReadI16();
        short objectID = r.ReadI16();
        var objectType = (IgcObjectType)r.ReadI16();
        short commandID = (short)(sbyte)r.ReadByte();   // sign-extend (native char is signed)
        short command = (short)(sbyte)r.ReadByte();
        return new OrderChange { ShipID = shipID, ObjectID = objectID, ObjectType = objectType, CommandId = commandID, Command = command };
    }
}
