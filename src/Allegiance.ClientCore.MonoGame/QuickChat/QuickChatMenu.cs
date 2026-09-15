using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.QuickChat;

/// <summary>
/// One node of the quick-chat menu tree (<c>QuickChatMenu</c>): a named
/// submenu whose choices are the <c>QuickChatMenuItem</c>s. A choice either
/// descends into another <see cref="QuickChatMenu"/> (the
/// <c>QuickChatMenuItem.node</c> is a <c>QuickChatMenu</c>) or is a leaf
/// command carrying a <see cref="QuickChatSendAction"/>.
/// </summary>
public sealed class QuickChatMenu
{
    /// <summary>The mdl symbol for this menu (e.g. <c>mainMenu</c>,
    /// <c>attackMenu</c>) — used to link submenu references.</summary>
    public string Name = "";

    /// <summary>The menu's choices in file order (top row = first).</summary>
    public List<QuickChatMenuChoice> Choices { get; } = new();

    /// <summary>Find the choice bound to a single-keypress shortcut
    /// (<c>QuickChatMenuItem.char</c>, the letter/digit shown before the
    /// caption). Case-sensitive, exactly matching the data — the top-level
    /// quick items use digits, the submenus use mixed-case letters.</summary>
    public QuickChatMenuChoice? ChoiceFor(char key)
    {
        foreach (var c in Choices)
            if (c.Key == key)
                return c;
        return null;
    }
}

/// <summary>One selectable row of a <see cref="QuickChatMenu"/>: the single-key
/// shortcut, the caption shown in the menu, and — depending on whether the
/// underlying <c>node</c> is a menu or a command — either a <see cref="Submenu"/>
/// to descend into or a leaf <see cref="Send"/> action. Exactly one of the two
/// is non-null.</summary>
public sealed class QuickChatMenuChoice
{
    /// <summary>The single-keypress shortcut (the <c>QuickChatMenuItem.char</c>
    /// field) — pressing this key selects the row.</summary>
    public char Key;

    /// <summary>The caption shown in the menu (the
    /// <c>QuickChatMenuItem.string</c> field). Note this is the UI label; the
    /// text actually <b>sent</b> is the leaf's <see cref="QuickChatSendAction.Entry"/>
    /// caption.</summary>
    public string Label = "";

    /// <summary>When this row is a submenu reference, the menu to descend into
    /// (<c>node</c> is a <c>QuickChatMenu</c>). Null for leaf commands.</summary>
    public QuickChatMenu? Submenu;

    /// <summary>When this row is a leaf command, the resolved send action
    /// (<c>node</c> is a <c>QuickChatCommand</c>). Null for submenus.</summary>
    public QuickChatSendAction? Send;
}

/// <summary>What a leaf quick-chat command sends on the wire: the resolved
/// <see cref="QuickChatEntry"/> (index → caption + sound) plus the
/// <see cref="ChatTarget"/>. The command's <c>targetType</c> mask in the mdl is
/// deliberately collapsed to a single target here (see
/// <see cref="QuickChatTarget.Resolve"/>).</summary>
public sealed class QuickChatSendAction
{
    /// <summary>The sonic chat behind this command — its <see cref="QuickChatEntry.Index"/>
    /// goes on the wire as <c>voiceOver</c>, its caption is sent as the message.</summary>
    public QuickChatEntry Entry = null!;

    /// <summary>The wire chat target for the send.</summary>
    public ChatTarget Target;
}
