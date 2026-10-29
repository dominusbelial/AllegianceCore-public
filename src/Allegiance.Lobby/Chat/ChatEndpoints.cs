using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Allegiance.Lobby.Identity;

namespace Allegiance.Lobby.Chat;

/// <summary>
/// P871 / lobby::Lobby chat — the authenticated directory-wide chat endpoints.
/// The wire behaviour mirrors native's <c>FM_CS_CHATMESSAGE</c> relay
/// (<c>clintlib/appmsg.cpp:2640-2665</c>): a signed-in sender posts to a channel,
/// every reader of that channel receives it once, the sender gets the relay
/// acknowledgement, and a reader's mute list suppresses a sender's lines.
/// </summary>
public static class ChatEndpoints
{
    public sealed record ChatPostRequest(string? Channel, string? Text);
    public sealed record ChatLineDto(long Sequence, DateTimeOffset At, string Channel,
        string SenderId, string Sender, string Text);
    public sealed record ChatPostResponse(long Sequence, ChatLineDto Line);
    public sealed record MuteRequest(string? AccountId);
    public sealed record ChatChannelDto(string Channel);

    public static void MapChat(this WebApplication app)
    {
        var group = app.MapGroup("/api/chat");

        // Native's relay POST needs an authenticated sender: the directory has no
        // anonymous chat (the game's own chat rides the game connection).
        group.MapPost("", (ChatPostRequest req, DirectoryChat chat, IdentityStore identities, HttpContext ctx) =>
        {
            if (identities.Authenticate(IdentityEndpoints.SessionSecret(ctx)) is not { } account)
                return Results.Unauthorized();
            string channel = string.IsNullOrWhiteSpace(req.Channel) ? DirectoryChat.DirectoryChannel : req.Channel!;
            if (chat.Post(account.Id, account.Callsign, channel, req.Text ?? "", out var error) is not { } line)
                return Results.BadRequest(new { error });
            return Results.Ok(new ChatPostResponse(line.Sequence, ToDto(line)));
        }).RequireRateLimiting(RateLimiting.ChatPolicy);

        // Reads are public like the server directory; a signed-in reader gets its
        // own mute filtering and no echo of its own lines.
        group.MapGet("", (DirectoryChat chat, IdentityStore identities, HttpContext ctx, string? channel, long? since) =>
        {
            string name = string.IsNullOrWhiteSpace(channel) ? DirectoryChat.DirectoryChannel : channel!;
            if (!DirectoryChat.IsValidChannel(name)) return Results.BadRequest(new { error = "unknown channel" });
            var account = identities.Authenticate(IdentityEndpoints.SessionSecret(ctx));
            var lines = chat.Read(name, Math.Max(0, since ?? 0), account?.Id);
            return Results.Ok(lines.Select(ToDto).ToArray());
        });

        group.MapGet("/channels", (DirectoryChat chat) =>
            Results.Ok(chat.Channels().Select(c => new ChatChannelDto(c)).ToArray()));

        group.MapPost("/mute", (MuteRequest req, DirectoryChat chat, IdentityStore identities, HttpContext ctx) =>
        {
            if (identities.Authenticate(IdentityEndpoints.SessionSecret(ctx)) is not { } account)
                return Results.Unauthorized();
            return string.IsNullOrWhiteSpace(req.AccountId) || !chat.Mute(account.Id, req.AccountId)
                ? Results.BadRequest(new { error = "a sender account id is required (self-mute is not allowed)" })
                : Results.Ok(new { muted = chat.Muted(account.Id) });
        }).RequireRateLimiting(RateLimiting.ChatPolicy);

        group.MapPost("/unmute", (MuteRequest req, DirectoryChat chat, IdentityStore identities, HttpContext ctx) =>
        {
            if (identities.Authenticate(IdentityEndpoints.SessionSecret(ctx)) is not { } account)
                return Results.Unauthorized();
            chat.Unmute(account.Id, req.AccountId ?? "");
            return Results.Ok(new { muted = chat.Muted(account.Id) });
        }).RequireRateLimiting(RateLimiting.ChatPolicy);
    }

    private static ChatLineDto ToDto(DirectoryChat.ChatLine line) =>
        new(line.Sequence, line.AtUtc, line.Channel, line.SenderAccountId, line.Sender, line.Text);
}
