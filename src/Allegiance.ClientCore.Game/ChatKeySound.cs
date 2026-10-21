using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// SIM-0317/0318/0319 — pure static decisions for when the native chat-key
/// <c>text</c> sound (sounddef.mdl:324) should play. Each decision mirrors the
/// compose-state gate at its native call site in <c>WinTrek/console.cpp</c>.
/// </summary>
public static class ChatKeySound
{
    /// <summary>
    /// Native <c>ConsoleImage::OnVerbButton</c> plays <c>chatKeySound</c> only
    /// when the compose state is away from the shell
    /// (<c>m_csComposeState != c_csComposeShell</c>, console.cpp:1266).
    /// </summary>
    public static bool OnVerb(NativeComposeState state) => state != NativeComposeState.Shell;

    /// <summary>
    /// Native <c>ConsoleImage::OnChar</c> plays <c>chatKeySound</c> for every
    /// typed character only while composing past the mouse-command state
    /// (<c>m_csComposeState > c_csComposeMouseCommand</c>, console.cpp:2337).
    /// The native enum order is NotComposing, MouseCommand, Chat, Command,
    /// Shell, Quick (console.cpp:524-529), which <see cref="NativeComposeState"/>
    /// preserves.
    /// </summary>
    public static bool OnChar(NativeComposeState state) => state > NativeComposeState.MouseCommand;

    /// <summary>
    /// Native <c>ConsoleImage::StartChat</c> plays <c>chatKeySound</c>
    /// unconditionally before opening the chat composer
    /// (console.cpp:2423).
    /// </summary>
    public static bool OnStartChat() => true;
}
