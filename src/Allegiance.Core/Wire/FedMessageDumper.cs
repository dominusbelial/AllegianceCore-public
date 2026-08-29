using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// The 136 numeric message ids (from <c>src/Inc/Messages.h</c>) mapped to their
/// <c>DEFINE_FEDMSG</c> short names. Ids are unique across the table (a bijection),
/// so a dictionary keyed by <c>fmid</c> is exact. The ~5 ids this port implements
/// are flagged <see cref="IsPorted"/>. Source of truth is the read-only
/// <c>Allegiance/</c> tree (<c>src/Inc/Messages.h</c>); names here match it.
/// </summary>
public static class FedMessageCatalog
{
    private static readonly Dictionary<ushort, string> Names = new()
    {
        [1]="LOGONREQ", [2]="LOGONACK", [3]="LOGOFF", [4]="BUY_LOADOUT",
        [5]="BUY_LOADOUT_ACK", [6]="OBJECT_SPOTTED", [7]="SET_CLUSTER", [8]="DEV_COMPLETED",
        [9]="SET_BRIEFING_TEXT", [10]="PLAYERINFO", [11]="STATIC_MAP_INFO", [12]="PROJECTILE_INSTANCE",
        [13]="RANK_INFO", [14]="SET_START_TIME", [15]="SET_TEAM_INFO", [16]="PLAYER_RESCUED",
        [17]="BALLOT", [18]="CANCEL_BALLOT", [19]="VOTE", [20]="SHIP_DELETE",
        [21]="RANDOMIZE_TEAMS", [23]="CHATMESSAGE", [24]="SHIP_UPDATE", [26]="EXPORT",
        [27]="POSTER", [28]="TREASURE", [30]="DESTROY_TREASURE", [31]="NEW_SHIP_INSTANCE",
        [33]="DROP_PART", [34]="SWAP_PART", [35]="PING", [36]="URLROOT",
        [37]="MISSION_STAGE", [38]="MISSIONDEF", [39]="POSITIONREQ", [40]="POSITIONREQ",
        [41]="POSITIONACK", [42]="DELPOSITIONREQ", [43]="LOCK_LOBBY", [44]="LOCK_SIDES",
        [45]="PLAYER_READY", [46]="TEAM_READY", [47]="FORCE_TEAM_READY", [48]="DOCKED",
        [51]="CHANGE_TEAM_CIV", [52]="AUTO_ACCEPT", [57]="STATIONS_UPDATE", [58]="STATION_CAPTURE",
        [59]="STATION_DESTROYED", [60]="MONEY_CHANGE", [61]="DOCKED", [62]="GAME_OVER",
        [63]="GAME_OVER_PLAYERS", [64]="BUCKET_DONATE", [65]="BUCKET_STATUS", [67]="PLAYER_DONATE",
        [68]="SIDE_INACTIVE", [70]="FIRE_MISSILE", [75]="SIDE_TECH_CHANGE", [76]="SIDE_ATTRIBUTE_CHANGE",
        [77]="EJECT", [78]="SINGLE_SHIP_UPDATE", [80]="SUICIDE", [82]="FIRE_EXPENDABLE",
        [83]="MISSILE_DESTROYED", [84]="MINE_DESTROYED", [85]="PROBE_DESTROYED", [86]="ASTEROID_DESTROYED",
        [87]="FIRE_EXPENDABLE", [89]="TREASURE_SETS", [90]="SHIP_STATUS", [92]="PROBES_UPDATE",
        [93]="ORDER_CHANGE", [94]="LEAVE_SHIP", [95]="JOINED_MISSION", [96]="LOADOUT_CHANGE",
        [99]="ACTIVE_TURRET_UPDATE", [100]="INACTIVE_TURRET_UPDATE", [102]="TELEPORT_ACK", [103]="BOARD_SHIP",
        [104]="VIEW_CLUSTER", [105]="KILL_SHIP", [106]="SET_WINGID", [107]="ICQ_CHAT_ACK",
        [108]="CHATBUOY", [109]="CREATE_CHAFF", [110]="MISSILE_SPOOFED", [111]="END_SPOOFING",
        [112]="AUTODONATE", [113]="MISSIONPARAMS", [114]="PAYDAY", [115]="SET_MONEY",
        [116]="AUTODONATE", [117]="MUTE", [121]="SET_TEAM_LEADER", [122]="SET_TEAM_INVESTOR",
        [123]="SET_MISSION_OWNER", [124]="QUIT_MISSION", [125]="JOIN_SIDE", [126]="QUIT_SIDE",
        [128]="ENTER_GAME", [129]="RELOAD", [131]="THREAT", [155]="GAME_STATE",
        [156]="GAIN_FLAG", [157]="START_GAME", [162]="ACQUIRE_TREASURE", [163]="TREASURE_ACK",
        [164]="ADD_PART", [166]="ENTER_LIFEPOD", [175]="LIGHT_SHIPS_UPDATE", [176]="HEAVY_SHIPS_UPDATE",
        [177]="VIEW_CLUSTER", [178]="BOARD_NACK", [179]="ASTEROIDS_UPDATE", [180]="ASTEROID_DRAINED",
        [181]="BUILDINGEFFECT_DESTROYED", [182]="REQUEST_MONEY", [183]="SHIP_RESET", [184]="RIPCORD_REQUEST",
        [185]="RIPCORD_ACTIVATE", [186]="RIPCORD_DENIED", [187]="RIPCORD_ABORTED", [188]="WARP_BOMB",
        [189]="PROMOTE", [190]="PROMOTE", [191]="CREATE_BUCKETS", [192]="RELAUNCH_SHIP",
        [193]="BANDWIDTH", [194]="PINGDATA", [195]="REQPINGDATA", [196]="CHANGE_ALLIANCE",
        [197]="CHANGE_ALLIANCES", [198]="ASTEROID_MINED", [199]="HIGHLIGHT_CLUSTER", [200]="CLUSTERINFO",
    };

    /// <summary>The message ids this port can fully decode (LOGONREQ/LOGONACK +
    /// the assign→command→observe slice).</summary>
    private static readonly HashSet<ushort> Ported = [1, 2, 7, 24, 78];

    public static string Name(ushort id) => Names.TryGetValue(id, out var n) ? n : "(unknown)";

    public static bool IsPorted(ushort id) => Ported.Contains(id);

    public static IReadOnlyCollection<ushort> PortedIds => Ported;
}

/// <summary>
/// The tolerant <b>decode path</b> shared by <c>tools/fedmsgdump</c> and its
/// tests. It reuses the shipped codec (<see cref="FedMsgCrc"/>,
/// <see cref="FedMessageHeader"/>, and each concrete message's
/// <see cref="IFedMessage"/>-shaped <c>Decode</c>); it does <b>not</b> reimplement
/// decoding. Unlike <see cref="FedPacket.Unpack"/> it never throws: it verifies
/// the CRC but continues on a mismatch, walks messages best-effort, and degrades
/// cleanly on anything it cannot fully decode — an unported id is printed with
/// its raw length + hex, a truncated buffer is reported as such, and a corrupt
/// cbmsg stops the walk with a pointer to the offending offset.
/// </summary>
public static class FedMessageDumper
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>Max raw bytes shown in a hex dump line.</summary>
    public const int HexShowMax = 64;

    /// <summary>A rendered packet plus its CRC verdict.</summary>
    /// <param name="CrcVerified">True when the trailing CRC over the message bytes matched.</param>
    /// <param name="Lines">The human-readable lines (header + per-message decode).</param>
    public sealed record PacketDump(bool CrcVerified, IReadOnlyList<string> Lines);

    /// <summary>Convenience wrapper returning just the rendered lines.</summary>
    public static IReadOnlyList<string> DumpPacket(byte[] packet) => DumpTyped(packet).Lines;

    /// <summary>Render a captured packet as human-readable lines. Never throws.</summary>
    public static PacketDump DumpTyped(byte[] packet)
    {
        var lines = new List<string>();

        if (packet.Length < FedPacket.CrcWidth)
        {
            lines.Add($"packet is only {packet.Length}B — shorter than the trailing {FedPacket.CrcWidth}-byte CRC slot; cannot be a valid packet");
            return new PacketDump(false, lines);
        }

        int msgBytes = packet.Length - FedPacket.CrcWidth;
        uint expected = BitConverter.ToUInt32(packet, msgBytes);
        uint actual = FedMsgCrc.Compute(packet.AsSpan(0, msgBytes));
        bool crcOk = actual == expected;
        lines.Add($"CRC {(crcOk ? "verified" : "FAILED")} over {msgBytes} message bytes " +
                  $"(computed 0x{actual:X8}, packet 0x{expected:X8})");

        if (!crcOk)
            lines.Add("  (continuing anyway — capture may be corrupt; messages are decoded best-effort)");

        int pos = 0;
        int index = 0;
        while (pos < msgBytes)
        {
            if (msgBytes - pos < FedMessageHeader.HeaderSize)
            {
                lines.Add($"* partial message header at offset {pos}: need {FedMessageHeader.HeaderSize} bytes, " +
                          $"only {msgBytes - pos} message bytes remain — truncation or corruption");
                break;
            }

            ushort cbmsg = BitConverter.ToUInt16(packet, pos);
            ushort fmid = BitConverter.ToUInt16(packet, pos + 2);
            if (cbmsg == 0 || pos + cbmsg > msgBytes)
            {
                lines.Add($"* malformed message at offset {pos}: cbmsg={cbmsg}, {msgBytes - pos} bytes remain " +
                          $"(fmid={fmid} '{FedMessageCatalog.Name(fmid)}') — stopping walk");
                lines.Add($"  (processed {index} message(s); stopped at offset {pos} of {msgBytes} message bytes)");
                return new PacketDump(crcOk, lines);
            }

            var msg = new byte[cbmsg];
            Array.Copy(packet, pos, msg, 0, cbmsg);
            lines.AddRange(FormatMessage(index++, pos, msg));
            pos += cbmsg;
        }

        if (index == 0) lines.Add("(no messages in this packet)");
        return new PacketDump(crcOk, lines);
    }

    private static IReadOnlyList<string> FormatMessage(int index, int offset, byte[] msg)
    {
        ushort fmid = BitConverter.ToUInt16(msg, 2);
        string name = FedMessageCatalog.Name(fmid);
        var lines = new List<string>
        {
            $"[msg {index} @ {offset}] fmid={fmid} cbmsg={msg.Length} ({name})",
        };

        if (!FedMessageCatalog.IsPorted(fmid))
        {
            lines.Add($"    id={fmid} ({name}) UNPORTED — {msg.Length} bytes, not decoded");
            lines.Add($"        {Hex(msg.AsSpan(0, Math.Min(msg.Length, HexShowMax)))}{(msg.Length > HexShowMax ? " …" : "")}");
            return lines;
        }

        string tag;
        try
        {
            tag = fmid switch
            {
                LogonRequest.Fmid => FormatLogonRequest(msg),
                LogonAck.Fmid => FormatLogonAck(msg),
                SetCluster.Fmid => FormatSetCluster(msg),
                ShipUpdate.Fmid => FormatShipUpdate(msg),
                SingleShipUpdate.Fmid => FormatSingleShipUpdate(msg),
                _ => "(ported id but no formatter registered)",
            };
            lines.AddRange(tag.Split('\n'));
            return lines;
        }
        catch (Exception e)
        {
            lines.Add($"    * decode failed midway: {e.GetType().Name}: {e.Message}");
            lines.Add($"        {Hex(msg.AsSpan(0, Math.Min(msg.Length, HexShowMax)))}{(msg.Length > HexShowMax ? " …" : "")}");
            return lines;
        }
    }

    // ── per-message formatters (field readers reuse the shipped Decode) ────

    private static string FormatLogonRequest(byte[] msg)
    {
        var r = LogonRequest.Decode(msg);
        var v = new StringBuilder();
        v.AppendLine("    (port LOGONREQ)");
        // Each var item: read the ib/cb descriptor from the wire, then the value.
        AppendVarString(v, 4, msg, "characterName", r.CharacterName);
        AppendVarBytes(v, 8, msg, "zoneTicket", r.ZoneTicket);
        AppendVarString(v, 12, msg, "cdKey", r.CDKey);
        AppendVarString(v, 16, msg, "missionPassword", r.MissionPassword);
        v.AppendLine($"    fedsrvVer={r.FedsrvVer}  time={r.Time}  dwCookie=0x{r.DwCookie:X8}  crcFileList={r.CrcFileList}");
        v.AppendLine($"    steamAuthTicket=<{r.SteamAuthTicket?.Length ?? 0} bytes>  steamAuthTicketLength={r.SteamAuthTicketLength}  " +
                     $"steamID={r.SteamID}  drmHash=\"{Esc(r.DrmHash)}\"");
        return v.ToString().TrimEnd();
    }

    private static string FormatLogonAck(byte[] msg)
    {
        var r = LogonAck.Decode(msg);
        var v = new StringBuilder();
        v.AppendLine("    (port LOGONACK)");
        AppendVarString(v, 4, msg, "charNameOrFailureReason", r.CharNameOrFailureReason);
        v.AppendLine($"    timeServer={r.TimeServer}  shipID={r.ShipID}  validated={r.FValidated}  " +
                     $"retry={r.FRetry}  cookie=0x{r.Cookie:X2}");
        return v.ToString().TrimEnd();
    }

    private static string FormatSetCluster(byte[] msg)
    {
        var r = SetCluster.Decode(msg);
        var s = r.Ship;
        return "    (port SET_CLUSTER)\n" +
               $"    sectorID={r.SectorId}  cookie=0x{r.Cookie:X2}  shipID={s.ShipId}\n" +
               $"    stateM=0x{s.StateM:X}  time={s.Time}  pos={s.Position}  vel={s.Velocity}\n" +
               $"    hull={s.HullFraction:F3}  shield={s.ShieldFraction:F3}  power={s.Power:F3}  controls={s.Controls}";
    }

    private static string FormatShipUpdate(byte[] msg)
    {
        var r = ShipUpdate.Decode(msg);
        var p = r.Payload;
        return "    (port SHIP_UPDATE)\n" +
               $"    time={r.Time}  cookie=0x{r.Cookie:X2}\n" +
               $"    stateM=0x{p.StateM:X}  controls={p.Controls}  " +
               $"  predPos={p.Position}";
    }

    private static string FormatSingleShipUpdate(byte[] msg)
    {
        var r = SingleShipUpdate.Decode(msg);
        var s = r.Ship;
        return "    (port SINGLE_SHIP_UPDATE)\n" +
               $"    otTarget={r.OtTarget}  oidTarget={r.OidTarget}  bIsRipcording={r.BIsRipcording}\n" +
               $"    shipID={s.ShipId}  stateM=0x{s.StateM:X}  time={s.Time}  pos={s.Position}  vel={s.Velocity}";
    }

    /// <summary>Print one <see cref="VarItem"/> descriptor + its value as a labelled
    /// line. <paramref name="slotOffset"/> is the offset of the item's <c>ib</c>
    /// within the message (its 2-short descriptor sits there).</summary>
    private static void AppendVarString(StringBuilder v, int slotOffset, byte[] msg, string label, string? value)
    {
        var it = VarItem.Read(msg.AsSpan(slotOffset, VarItem.ItemSize));
        v.Append($"    {label}: ib={it.Ib} cb={it.Cb}");
        v.Append(it.IsPresent ? $"  \"{Esc(value)}\"" : "  (absent)");
        v.Append('\n');
    }

    private static void AppendVarBytes(StringBuilder v, int slotOffset, byte[] msg, string label, byte[]? value)
    {
        var it = VarItem.Read(msg.AsSpan(slotOffset, VarItem.ItemSize));
        v.Append($"    {label}: ib={it.Ib} cb={it.Cb}");
        if (it.IsPresent && value is not null)
            v.Append($"  <{value.Length} bytes> {Hex(value, 16)}{(value.Length > 16 ? " …" : "")}");
        else
            v.Append("  (absent)");
        v.Append('\n');
    }

    private static string Esc(string? s) => s ?? "";

    /// <summary>Hex of up to <paramref name="max"/> bytes, lowercase, space-separated octets.</summary>
    public static string Hex(ReadOnlySpan<byte> data, int max = HexShowMax)
    {
        int n = Math.Min(data.Length, max);
        var sb = new StringBuilder(n * 3);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }
}
