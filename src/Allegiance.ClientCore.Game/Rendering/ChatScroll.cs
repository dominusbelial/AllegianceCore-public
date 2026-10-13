namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P626 — pure chat-buffer scroll model for the in-flight chat pane.
///
/// The chat log is a bottom-anchored window of the newest
/// <see cref="ChatRenderer.MaxVisible"/> lines. Scrolling looks back into the
/// buffer by an <em>offset</em> (number of lines pulled from the newest edge).
/// The four native commands map onto it:
///   <list type="bullet">
///     <item><c>TK_PrevCommMsg</c> (igc.h:227 "scroll focus to previous chat
///     message") and <c>TK_ChatPageUp</c> (igc.h:328 "scroll the chat pane one
///     page up") move <em>older</em> (offset increases);</item>
///     <item><c>TK_NextCommMsg</c> (igc.h:228) and <c>TK_ChatPageDown</c>
///     (igc.h:329) move <em>newer</em> (offset decreases).</item>
///   </list>
/// The window the pane draws is <c>[count - MaxVisible - offset,
/// count - offset)</c>, so offset 0 = newest visible and offset
/// <c>max(0, count - MaxVisible)</c> = oldest reachable. All arithmetic is
/// clamped so a spammed key can never underflow or overrun.
/// </summary>
public static class ChatScroll
{
    /// <summary>Clamp a raw offset into the valid range for <paramref name="count"/>
    /// chat lines at a pane that shows <paramref name="visible"/> lines.</summary>
    public static int Clamp(int offset, int count, int visible)
    {
        int max = Math.Max(0, count - Math.Max(1, visible));
        return Math.Clamp(offset, 0, max);
    }

    /// <summary>Oldest line index the pane should draw given the scroll offset.</summary>
    public static int StartIndex(int offset, int count, int visible)
        => Math.Max(0, count - Math.Max(1, visible) - Clamp(offset, count, visible));

    /// <summary>Whether the buffer is scrollable at all (more lines than fit).</summary>
    public static bool CanScroll(int count, int visible) => count > Math.Max(1, visible);

    /// <summary>Step one line older (<c>TK_PrevCommMsg</c>).</summary>
    public static int StepOlder(int offset, int count, int visible)
        => Clamp(offset + 1, count, visible);

    /// <summary>Step one line newer (<c>TK_NextCommMsg</c>).</summary>
    public static int StepNewer(int offset, int count, int visible)
        => Clamp(offset - 1, count, visible);

    /// <summary>Page older by one pane-full (<c>TK_ChatPageUp</c>).</summary>
    public static int PageOlder(int offset, int count, int visible)
        => Clamp(offset + Math.Max(1, visible), count, visible);

    /// <summary>Page newer by one pane-full (<c>TK_ChatPageDown</c>).</summary>
    public static int PageNewer(int offset, int count, int visible)
        => Clamp(offset - Math.Max(1, visible), count, visible);
}
