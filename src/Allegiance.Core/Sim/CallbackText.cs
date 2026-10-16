using System.Globalization;

namespace Allegiance.Core.Sim;

/// <summary>Managed counterpart of the 256-byte formatting buffers in
/// Igc/igc.h:4817-4825 and WinTrek/trekigc.cpp:2999-3005,3091-3100.
/// Callers use .NET composite formats; plain text never passes through a formatter.
/// The native unterminated-buffer overflow is not reproduced.</summary>
public static class CallbackText
{
    public static string Format(string format, params object?[] args)
    {
        string text = string.Format(CultureInfo.InvariantCulture, format, args);
        // Reserve the native buffer's terminator. Avoid splitting a UTF-16 pair.
        int length = Math.Min(text.Length, 255);
        if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }
}
