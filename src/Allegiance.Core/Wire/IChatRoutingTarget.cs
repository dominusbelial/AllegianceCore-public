namespace Allegiance.Core.Wire;

/// <summary>
/// The subset of a chat-family message the server's relay routing reads:
/// the target kind (<c>chatTarget</c>) and the addressed object
/// (<c>oidRecipient</c>). Both <see cref="ChatMessage"/>(23) and
/// <see cref="ChatBuoy"/>(108) carry identical <see cref="ChatData cd"/>
/// blocks (<c>Messages.h:216-222</c>, <c>Messages.h:716-722</c>), so
/// <c>SelectChatTargets</c> accepts this interface instead of a concrete type,
/// letting the CHATBUOY handler reuse the exact same routing method verbatim
/// (no duplicated switch, no narrowed copy). The only fields
/// <c>SelectChatTargets</c> (<c>src/Allegiance.Server/Server.cs:1419</c>) ever
/// reads are <see cref="ChatTarget"/> and <see cref="OidRecipient"/>.
/// </summary>
public interface IChatRoutingTarget
{
    /// <summary>Raw wire <c>chatTarget</c> (a 4-byte C++ enum, igc.h:2729).</summary>
    int ChatTarget { get; }

    /// <summary>Addressed <c>oidRecipient</c> (ObjectID short, igc.h:477).</summary>
    short OidRecipient { get; }
}
