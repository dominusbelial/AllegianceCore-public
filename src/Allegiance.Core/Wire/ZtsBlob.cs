using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// Shared CB_ZTS blob handling for the chat family of messages
/// (<see cref="ChatMessage"/> fmid 23 and <see cref="ChatBuoy"/> fmid 108 both
/// carry a trailing <c>Message</c> CB_ZTS string, <c>Messages.h:216-222</c> and
/// <c>Messages.h:716-722</c>). Factored out of what was ChatMessage's own private
/// helpers so the two codecs reuse identical logic instead of duplicating it
/// (FEDMSG.md §2, messagecore.h:618, Messages.cpp:372-398).
/// </summary>
public static class ZtsBlob
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>CB_ZTS blob length: trimmed length + 1 for the NUL.</summary>
    public static int BlobSize(string s) => TrimTrailingSpaces(s) + 1;

    private static int TrimTrailingSpaces(string s)
    {
        int n = s.Length;
        while (n > 0 && s[n - 1] == ' ') n--;
        return n;
    }

    /// <summary>Write the trimmed, NUL-terminated string at the writer's current
    /// position. <c>cb</c> includes the NUL; the blob's <c>ib</c> is the offset
    /// from message byte 0, matching <c>PFedMsgCreate</c>.</summary>
    public static (ushort Ib, ushort Cb) Write(ref WireWriter w, string s)
    {
        int n = TrimTrailingSpaces(s);
        int ib = w.Position;
        if (n > 0) w.WriteBytes(Latin1.GetBytes(s.Substring(0, n)));
        w.WriteByte(0);
        return ((ushort)ib, (ushort)(n + 1));
    }

    /// <summary>Read a CB_ZTS blob referenced by <paramref name="it"/>, dropping
    /// the trailing NUL (a <c>cb</c>-0 "absent" item yields the empty string).</summary>
    public static string Read(ReadOnlySpan<byte> msg, VarItem it)
    {
        var data = msg.Slice(it.Ib, it.Cb);
        if (data.Length > 0 && data[^1] == 0) data = data[..^1];
        return Latin1.GetString(data);
    }
}
