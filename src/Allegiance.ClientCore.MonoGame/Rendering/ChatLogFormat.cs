using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Pure, side-effect-free chat-line formatting — no MonoGame types, so it is
/// unit-testable without a graphics device (mirroring the <see cref="Flight"/>
/// / <see cref="HudFormat"/> discipline). Turns a raw relayed
/// <c>ChatMessage</c>'s parts into the one display string the chat log draws:
/// an optional target tag, the sender name, then the message text.
/// </summary>
public static class ChatLogFormat
{
    /// <summary>
    /// Compose the display line for one chat. <paramref name="sender"/> is the
    /// resolved display name (empty when unknown — the message stands alone),
    /// <paramref name="target"/> drives the channel tag. Everyone (broadcast)
    /// has no tag; the non-broadcast channels the server actually relays get a
    /// short bracketed tag so the reader can tell channels apart at a glance.
    /// </summary>
    public static string Format(string sender, string message, ChatTarget target)
    {
        var tag = TargetTag(target);
        if (string.IsNullOrEmpty(sender))
            return AsciiSafe(tag + message);
        return AsciiSafe($"{tag}{sender}: {message}");
    }

    /// <summary>
    /// The HUD spritefonts bake only the ASCII printable range (32..126), so any
    /// char outside it must not reach <c>DrawString</c> (it throws). Map stray
    /// high/control characters to '?' rather than crashing the renderer on a
    /// foreign-language or binary chat line.
    /// </summary>
    public static string AsciiSafe(string s)
    {
        if (s.All(c => c >= 32 && c <= 126))
            return s;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c >= 32 && c <= 126 ? c : '?');
        return sb.ToString();
    }

    /// <summary>The bracketed channel tag for a chat target, or "" for the
    /// Everyone broadcast (and anything unrecognised — never a crash on a
    /// weird wire value).</summary>
    public static string TargetTag(ChatTarget target) => target switch
    {
        ChatTarget.Team            => "[TEAM] ",
        ChatTarget.Allies          => "[ALLIES] ",
        ChatTarget.Leaders         => "[LEADERS] ",
        ChatTarget.Group           => "[GROUP] ",
        ChatTarget.AllSector       => "[SECTOR] ",
        ChatTarget.FriendlySector  => "[FSECTOR] ",
        ChatTarget.Admin           => "[ADMIN] ",
        _                          => "",
    };
}
