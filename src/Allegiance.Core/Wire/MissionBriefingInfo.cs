using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.Core.Wire;

/// <summary>
/// P759 port extension (like <see cref="ResearchBriefing"/>; see item 3 of the P759b
/// review: this message is <b>port-only</b> — native reads the mission parameters
/// client-side from the live <c>MissionParams</c>/side/civilization objects, so no
/// native counterpart exists on the wire): the client-side inputs native's
/// <c>GetCivMissionBriefing</c> reads
/// (<c>WinTrek/teamscreen.cpp:732-793</c>, <c>:820-826</c>) — side name, civ name and
/// its mdl string prefix (<c>GetCivilization()-&gt;GetIconName()</c>,
/// <c>teamscreen.cpp:636-639</c>), the briefing text (<c>trekClient.GetBriefingText()</c>),
/// the civ list (<c>GetCivListString()</c>) and the mission win conditions — sent once
/// in the side join-sync burst next to <c>SET_BRIEFING_TEXT(9)</c>. Native reads these
/// from <c>MissionParams</c> and the live side/civilization objects. This port
/// decodes goal parameters in <see cref="MissionDef"/> and also forwards their
/// authoritative briefing projection with the side/civilization text inputs.
/// Brotli-compressed
/// source-generated JSON, AOT-safe (never reflection serialization).
/// </summary>
public sealed class MissionBriefingInfo : IFedMessage
{
    public const ushort Fmid = 996;

    public string SideName { get; set; } = "";
    public string CivName { get; set; } = "";
    /// <summary>The civilization's mdl string prefix (native concatenates
    /// <c>GetCivilization()-&gt;GetIconName()</c> before each briefing key,
    /// <c>teamscreen.cpp:636-639</c>; the strings themselves live in the artwork's
    /// <c>missionbrief.mdl</c> and are read by the client from the artwork corpus).</summary>
    public string CivIconName { get; set; } = "";
    public string BriefingText { get; set; } = "";
    public List<string> CivList { get; set; } = [];

    /// <summary>No win conditions at all (native <c>IsNoWinningConditionsGame()</c>).</summary>
    public bool NoWin { get; set; }

    public bool Conquest { get; set; }
    public int ConquestPercent { get; set; }
    public bool Artifacts { get; set; }
    public int ArtifactGoal { get; set; }
    public bool Flags { get; set; }
    public int FlagsGoal { get; set; }
    public bool Deathmatch { get; set; }
    public int KillLimit { get; set; }
    /// <summary>Native <c>dtGameLength</c> in seconds; non-zero means
    /// <c>IsCountdownGame()</c> (igc.h:1526, 1553-1556) and the briefing shows
    /// <c>(int)(GetCountDownTime() / 60)</c> minutes (teamscreen.cpp:773-777).
    /// Zero when the mission has no time limit.</summary>
    public double CountdownSeconds { get; set; }
    public bool Prosperity { get; set; }
    public int ProsperityMoney { get; set; }

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this, MissionBriefingInfoJsonContext.Default.MissionBriefingInfo);
        using var output = new MemoryStream();
        output.Write(new byte[4]);
        using (var zip = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true)) zip.Write(json);
        var bytes = output.ToArray();
        if (bytes.Length > ushort.MaxValue) throw new InvalidDataException("mission briefing info exceeds wire limit");
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Fmid);
        return bytes;
    }

    public static MissionBriefingInfo Decode(ReadOnlySpan<byte> bytes)
    {
        var header = FedMessageHeader.Read(bytes);
        if (header.Fmid != Fmid || header.CbMsg != bytes.Length) throw new ArgumentException("invalid mission briefing info header");
        using var input = new MemoryStream(bytes[4..].ToArray());
        using var zip = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = zip.Read(buffer)) > 0)
        {
            if (output.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("mission briefing info too large");
            output.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize(output.ToArray(), MissionBriefingInfoJsonContext.Default.MissionBriefingInfo)
            ?? throw new InvalidDataException("empty mission briefing info");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MissionBriefingInfo))]
public partial class MissionBriefingInfoJsonContext : JsonSerializerContext;
