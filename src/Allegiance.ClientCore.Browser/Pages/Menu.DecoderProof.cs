using System.IO.Compression;
using Microsoft.JSInterop;

namespace Allegiance.ClientCore.Browser.Pages;

public partial class Menu
{
    [JSInvokable]
    public byte[] DecodeBrotli(byte[] input) => _briefingDecoder!.Invoke<byte[]>("decode", input);

    // Read-only runtime capability evidence for the loopback Chromium harness.
    [JSInvokable]
    #pragma warning disable CA1416 // Intentionally measure every codec under exception containment.
    public string[] ProbeBrotli(byte[] message)
    {
        string decoder, stream;
        try { var output = new byte[4 * 1024 * 1024]; decoder = $"BrotliDecoder: {BrotliDecoder.TryDecompress(message.AsSpan(4), output, out int written)}, {written} bytes"; }
        catch (Exception error) { decoder = $"BrotliDecoder: {error.GetType().Name}: {error.Message}"; }
        try { using var input = new MemoryStream(message, 4, message.Length - 4); using var zip = new BrotliStream(input, CompressionMode.Decompress); using var output = new MemoryStream(); zip.CopyTo(output); stream = $"BrotliStream: {output.Length} bytes"; }
        catch (Exception error) { stream = $"BrotliStream: {error.GetType().Name}: {error.Message}"; }
        return [decoder, stream];
    }
    /// <summary>P1073 — prove which codecs the real browser runtime actually supports before
    /// choosing how the port-only research briefing crosses the wire. The browser build
    /// ships neither BrotliStream nor BrotliDecoder, so the resident P1051 runtime
    /// decoder (staged from the installed .NET runtime's MIT Google Brotli objects) is
    /// the only Brotli-capable path; Deflate/GZip are reported for comparison only and
    /// are not used (desktop clients keep the unchanged Brotli wire format).</summary>
    [JSInvokable]
    public object ProbeCodecs(byte[] message)
    {
        // The caller supplies a real Brotli-compressed fmid 992 frame: the browser runtime
        // cannot compress Brotli either, so the frame is built outside the page (the
        // harness uses Node's zlib). Desktop encodes the same frame with BrotliStream.
        var brotli = ProbeBrotli(message);
        string deflate, gzip;
        try
        {
            using var input = new DeflateStream(new MemoryStream(message, 4, message.Length - 4), CompressionMode.Decompress);
            using var output = new MemoryStream(); input.CopyTo(output); deflate = $"DeflateStream: {output.Length} bytes";
        }
        catch (Exception error) { deflate = $"DeflateStream: {error.GetType().Name}: {error.Message}"; }
        try
        {
            using var input = new GZipStream(new MemoryStream(message, 4, message.Length - 4), CompressionMode.Decompress);
            using var output = new MemoryStream(); input.CopyTo(output); gzip = $"GZipStream: {output.Length} bytes";
        }
        catch (Exception error) { gzip = $"GZipStream: {error.GetType().Name}: {error.Message}"; }
        bool runtime = false;
        string runtimeResult;
        try
        {
            if (_briefingDecoder is not null)
            {
                byte[] json = _briefingDecoder.Invoke<byte[]>("decode", message[4..]);
                runtime = json.Length > 0; runtimeResult = $"resident decoder: {json.Length} bytes";
            }
            else runtimeResult = "resident decoder: not staged";
        }
        catch (Exception error) { runtimeResult = $"resident decoder: {error.GetType().Name}: {error.Message}"; }
        return new { brotliDecoder = brotli[0], brotliStream = brotli[1], deflate, gzip,
            residentDecoder = runtimeResult, residentDecoderWorks = runtime };
    }
    #pragma warning restore CA1416
}
