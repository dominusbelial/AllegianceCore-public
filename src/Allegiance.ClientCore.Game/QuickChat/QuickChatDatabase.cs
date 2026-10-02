namespace Allegiance.ClientCore.MonoGame.QuickChat;

/// <summary>
/// The in-memory result of parsing <c>quickchat.mdl</c> + <c>sounddef.mdl</c>:
/// the wire-index→entry table (used to resolve a received <c>voiceOver</c> to a
/// caption + sound) and the nested menu tree rooted at <see cref="MainMenu"/>
/// (used to drive the on-screen quick-chat menu and its leaf sends).
/// </summary>
public sealed class QuickChatDatabase
{
    /// <summary>The wire <c>voiceOver</c> index → <see cref="QuickChatEntry"/>.
    /// Keys are the explicit indices from the <c>sonicChats</c> list (not
    /// contiguous — holes are simply absent).</summary>
    public IReadOnlyDictionary<int, QuickChatEntry> EntriesByIndex { get; init; }
        = new Dictionary<int, QuickChatEntry>();

    /// <summary>Every named menu, keyed by its mdl symbol. Includes
    /// <see cref="MainMenu"/> itself and every submenu it reaches.</summary>
    public IReadOnlyDictionary<string, QuickChatMenu> MenusByName { get; init; }
        = new Dictionary<string, QuickChatMenu>();

    /// <summary>The top-level quick-chat menu (<c>mainMenu</c>), or null if the
    /// parse did not yield one.</summary>
    public QuickChatMenu? MainMenu { get; init; }

    /// <summary>Resolve a received <c>voiceOver</c> index to its entry, or null
    /// when the index is not in the table (a hole, an out-of-range value, or a
    /// chat index this dataset does not define).</summary>
    public QuickChatEntry? ByVoiceOver(int voiceOver) =>
        EntriesByIndex.TryGetValue(voiceOver, out var e) ? e : null;
}
