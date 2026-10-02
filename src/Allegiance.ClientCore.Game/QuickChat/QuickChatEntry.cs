namespace Allegiance.ClientCore.MonoGame.QuickChat;

/// <summary>
/// One resolved quick-chat voice (<c>SonicChat</c>) entry. Carries everything
/// the wire + UI need: the <see cref="Index"/> that travels as the
/// <c>voiceOver</c> field of <c>CHATMESSAGE(23)</c> (the explicit index from
/// <c>quickchat.mdl</c>'s <c>sonicChats</c> list, matching native's
/// <c>m_vsonicChats[id]</c>), the <see cref="Caption"/> text that is sent and
/// shown in the chat log, and the <see cref="SoundStem"/> of the real
/// <c>.ogg</c> asset played when it is received.
/// </summary>
/// <remarks>
/// <para>Pure data — no MonoGame types, so the parser that produces it is
/// unit-testable without a graphics device (same discipline as
/// <see cref="Rendering.ChatLogFormat"/> / <see cref="Flight.FlightInput"/>).</para>
/// <para>Native sends a quick-chat with <c>pszText = NULL</c>
/// (<c>SendChatInternal(NULL, idSonicChat, …)</c>, console.cpp:1593) and the
/// receiver derives the display text from the index via
/// <c>GetSonicChatText(idSonicChat, 0)</c> (trekigc.cpp:4514). This port's
/// wire needs real text, so it sends the <see cref="Caption"/> as the message
/// — identical displayed text, same round-trip guarantee.</para>
/// </remarks>
public sealed class QuickChatEntry
{
    /// <summary>The wire <c>voiceOver</c> value for this chat (SoundID short,
    /// igc.h:453). Unique across the dataset; an explicit index from
    /// <c>quickchat.mdl</c>'s <c>sonicChats</c> list.</summary>
    public int Index;

    /// <summary>The spoken caption — sent as the chat message and shown in the
    /// log (the <c>SonicChatVoice.string</c> member).</summary>
    public string Caption = "";

    /// <summary>The real <c>.ogg</c> asset stem resolved from
    /// <c>sounddef.mdl</c>'s <c>ImportWave(...)</c> for the voice's sound
    /// identifier (e.g. <c>voAttackSound</c> → <c>vo_player_attack</c>).
    /// Empty when the sound identifier has no resolvable/importable wave (the
    /// caption still displays; only the audio is skipped).</summary>
    public string SoundStem = "";
}
