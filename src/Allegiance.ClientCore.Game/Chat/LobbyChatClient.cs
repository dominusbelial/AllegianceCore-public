using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame.Chat;

/// <summary>
/// P871 / lobby::Lobby chat — the HTTP client for the lobby's authenticated
/// directory chat. Reads are public like the server directory; posting and
/// muting need the signed-in lobby session, so every mutating request carries the
/// same bearer token the identity client issues (<c>IdentityEndpoints</c>
/// accepts it via <c>SessionSecret</c>) — P871 sent none, so the lobby rejected
/// every post with 401.
///
/// <para><b>Native scope (P871b).</b> Native has no lobby/directory chat at all:
/// every chat target is an in-mission one (<c>Igc/igc.h:2731-2734</c>:
/// CHAT_EVERYONE…CHAT_FRIENDLY_SECTOR) and the lobby only receives
/// <c>FM_S_LOG_CHAT_MESSAGE(216)</c> for moderation logging
/// (<c>lobby/messagesls.h:152</c>, <c>lobby/server.cpp:341-372</c>). Directory
/// chat is therefore a documented PORT EXTENSION, not a parity behaviour; the
/// semantics it borrows from native are the in-mission relay's (sender echo
/// suppression, per-player mute, the shell <c>!mute</c> command).</para>
/// </summary>
public sealed class LobbyChatClient
{
    public sealed record PostBody(string Channel, string Text);
    public sealed record MuteBody(string AccountId);
    public sealed record LineDto(long Sequence, DateTimeOffset At, string Channel,
        string SenderId, string Sender, string Text);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly string _channel;
    private readonly Func<string?>? _bearer;

    public LobbyChatClient(HttpClient http, string lobbyUrl, string channel = "directory",
        Func<string?>? bearer = null)
    {
        _http = http;
        _channel = channel;
        _bearer = bearer;
        _base = new Uri(lobbyUrl.EndsWith('/') ? lobbyUrl : lobbyUrl + "/", UriKind.Absolute);
    }

    public string Channel => _channel;

    /// <summary>Attach the signed-in lobby session (identity bearer) to a request.
    /// Native's chat carried the sending player's session; the port's lobby
    /// authenticates the HTTP bearer the same way.</summary>
    private void Authorize(HttpRequestMessage request)
    {
        if (_bearer?.Invoke() is { Length: > 0 } token)
            request.Headers.Authorization = new("Bearer", token);
    }

    /// <summary>Poll the channel after the log's cursor. Returns the accepted
    /// lines (null when the lobby is unreachable — the caller keeps the log).</summary>
    public async Task<IReadOnlyList<DirectoryChatLine>?> PollAsync(DirectoryChatLog log,
        string? ownAccountId, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(_base, $"api/chat?channel={Uri.EscapeDataString(_channel)}&since={log.Cursor}"));
            Authorize(request);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var lines = await response.Content.ReadFromJsonAsync<LineDto[]>(Json, ct).ConfigureAwait(false);
            if (lines is null) return null;
            var relayed = lines.Select(l => new DirectoryChatLine(l.Sequence, l.Channel, l.SenderId,
                l.Sender, l.Text, l.At));
            return log.Accept(relayed, ownAccountId);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Post a line. Returns the accepted sequence (relay feedback) or
    /// null when the lobby refused it / is unreachable.</summary>
    public async Task<long?> SendAsync(string text, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "api/chat"))
            {
                Content = JsonContent.Create(new PostBody(_channel, text), options: Json),
            };
            Authorize(request);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var ok = await response.Content.ReadFromJsonAsync<PostOk>(Json, ct).ConfigureAwait(false);
            return ok?.Sequence;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Mute a sender for this account (native chat-pane/context-menu mute,
    /// <c>WinTrek/WinTrek.cpp:3731-3734</c>, <c>clintlib/clintlib.h:326</c>).
    /// The port persists the mute on the lobby account so it survives a
    /// reconnect; the local log drops the sender immediately either way.</summary>
    public async Task<bool> MuteAsync(string senderAccountId, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "api/chat/mute"))
            {
                Content = JsonContent.Create(new MuteBody(senderAccountId), options: Json),
            };
            Authorize(request);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Un-mute a sender previously muted on the lobby account.</summary>
    public async Task<bool> UnmuteAsync(string senderAccountId, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "api/chat/unmute"))
            {
                Content = JsonContent.Create(new MuteBody(senderAccountId), options: Json),
            };
            Authorize(request);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    private sealed record PostOk(long Sequence);
}
