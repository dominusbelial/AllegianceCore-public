using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Allegiance.Core.Model;

namespace Allegiance.Core.Wire;

/// <summary>P703 port extension: read-only selected-plan briefing, sent with side join sync.
/// Native has no research scripts. Execution still uses native BUCKET_DONATE and chat orders.
/// Brotli keeps authored plans (including unlock descriptions) within one ushort-sized message.</summary>
public sealed class ResearchBriefing : IFedMessage
{
    public const ushort Fmid = 992;
    public string Epoch { get; set; } = "";
    public short SideId { get; set; }
    public ResearchPlan Plan { get; set; } = new();
    public ResearchSettings Settings { get; set; } = new();
    public double IncomeMultiplier { get; set; } = 1;
    public bool Loop { get; set; }
    public List<short> CompletedDevelopments { get; set; } = [];
    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this, ResearchBriefingJsonContext.Default.ResearchBriefing);
        using var output = new MemoryStream();
        output.Write(new byte[4]);
        using (var zip = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true)) zip.Write(json);
        var bytes = output.ToArray();
        if (bytes.Length > ushort.MaxValue) throw new InvalidDataException("research briefing exceeds wire limit");
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Fmid);
        return bytes;
    }

    public static ResearchBriefing Decode(ReadOnlySpan<byte> bytes)
    {
        var header = FedMessageHeader.Read(bytes);
        if (header.Fmid != Fmid || header.CbMsg != bytes.Length) throw new ArgumentException("invalid research briefing header");
        using var input = new MemoryStream(bytes[4..].ToArray());
        using var zip = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = zip.Read(buffer)) > 0)
        {
            if (output.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("research briefing too large");
            output.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize(output.ToArray(), ResearchBriefingJsonContext.Default.ResearchBriefing)
            ?? throw new InvalidDataException("empty research briefing");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ResearchBriefing))]
public partial class ResearchBriefingJsonContext : JsonSerializerContext;
