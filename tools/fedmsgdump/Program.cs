using Allegiance.Core.Wire;

//
// fedmsgdump — decode captured FEDMSG packets to readable lines.
//
// Usage:
//   dotnet run --project tools/fedmsgdump -- CAPTURE_FILE [CAPTURE_FILE ...]
//   dotnet run --project tools/fedmsgdump -- -            # hex on stdin
//
// A capture file is the binary output of the server's `--capture <path>`
// option: a sequence of frames, each `dir(1) + len(u32 LE) + packet`, where a
// packet is one-or-more messages plus the trailing 4-byte CRC (see
// CaptureStream.cs). `-` reads stdin and interprets it as hex of the same
// bytes. Every packet is printed as a direction banner plus one decoded line
// per message; unported ids and anything that cannot be decoded fully are
// reported rather than crashing. Exit code is 0 only when every input parsed
// cleanly and every packet's CRC verified.
//
int bad = 0;
try
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: fedmsgdump <capture.bin> [... | - for hex on stdin]");
        return 2;
    }

    bool anyInput = false;
    foreach (var arg in args)
    {
        var label = arg == "-" ? "<stdin hex>" : arg;
        byte[] bytes;
        if (arg == "-")
        {
            anyInput = true;
            var text = Console.In.ReadToEnd();
            bytes = HexDecode(text);
            if (bytes.Length == 0)
            {
                Console.Error.WriteLine("no hex on stdin");
                bad++;
                continue;
            }
        }
        else
        {
            if (!File.Exists(arg)) { Console.Error.WriteLine($"missing: {arg}"); bad++; continue; }
            anyInput = true;
            bytes = File.ReadAllBytes(arg);
        }

        if (!ProcessSource(label, bytes, ref bad))
        {
            Console.Error.WriteLine($"{label}: trailing garbage / unresolved frame — non-zero exit");
        }
    }

    if (!anyInput) { Console.Error.WriteLine("no inputs processed"); bad++; }
}
catch (Exception e)
{
    Console.Error.WriteLine($"fedmsgdump failed: {e}");
    bad++;
}
return bad == 0 ? 0 : 1;

// ── helpers (file-scoped; top-level statements must declare above the returns
//    they need, so these are static locals used before declaration is fine) ──

bool ProcessSource(string label, byte[] bytes, ref int badOut)
{
    var capture = CaptureStream.ReadAll(bytes);
    int frames = capture.Frames.Count;
    int verified = 0;
    Console.WriteLine($"== {label}: {frames} frame(s){(capture.Truncated ? ", TRUNCATED/CORRUPT" : "")} ==");
    foreach (var fr in capture.Frames)
    {
        Console.WriteLine($"[{DirectionLabel(fr.Direction)}] {fr.Packet.Length} bytes");
        var dump = FedMessageDumper.DumpTyped(fr.Packet);
        foreach (var line in dump.Lines) Console.WriteLine(line);
        if (dump.CrcVerified) verified++;
    }
    if (capture.Truncated)
    {
        Console.Error.WriteLine($"{label}: {capture.Diagnostic} at offset {capture.Offset} — " +
                                $"decoded {frames} frame(s) up to that point");
        badOut++;
    }
    if (frames > 0 && verified < frames)
    {
        Console.Error.WriteLine($"{label}: {frames - verified} packet(s) failed CRC");
        badOut++;
    }
    return !capture.Truncated;
}

static string DirectionLabel(CaptureDirection d) =>
    d == CaptureDirection.Send ? "TX server→client" : "RX client→server";

static byte[] HexDecode(string text)
{
    var sb = new System.Text.StringBuilder();
    foreach (var c in text) if (!char.IsWhiteSpace(c)) sb.Append(c);
    var hex = sb.ToString();
    if ((hex.Length & 1) != 0) hex = "0" + hex;          // tolerate an odd nibble
    var bytes = new byte[hex.Length / 2];
    for (int i = 0; i < bytes.Length; i++)
        bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
    return bytes;
}
