using System.IO.Compression;
using Microsoft.JSInterop;

namespace Allegiance.ClientCore.Browser.Pages;

public partial class Menu
{
    [JSInvokable]
    public byte[] DecodeBrotli(byte[] input) => _briefingDecoder!.Invoke<byte[]>("decode", input);

    // Read-only runtime capability evidence for the loopback Chromium harness.
    [JSInvokable]
    #pragma warning disable CA1416 // Intentionally measure both unsupported browser APIs under exception containment.
    public string[] ProbeBrotli(byte[] message)
    {
        string decoder, stream;
        try { var output = new byte[4 * 1024 * 1024]; decoder = $"BrotliDecoder: {BrotliDecoder.TryDecompress(message.AsSpan(4), output, out int written)}, {written} bytes"; }
        catch (Exception error) { decoder = $"BrotliDecoder: {error.GetType().Name}: {error.Message}"; }
        try { using var input = new MemoryStream(message, 4, message.Length - 4); using var zip = new BrotliStream(input, CompressionMode.Decompress); using var output = new MemoryStream(); zip.CopyTo(output); stream = $"BrotliStream: {output.Length} bytes"; }
        catch (Exception error) { stream = $"BrotliStream: {error.GetType().Name}: {error.Message}"; }
        return [decoder, stream];
    }
    #pragma warning restore CA1416
}
