using System.Net;
using System.Text;
using Allegiance.Core.Identity;
using Allegiance.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P1024 (R1 F9) — the server's reserved-callsign cache compares the same
/// <see cref="JoinTokens.CallsignSkeleton"/> the lobby's account table uses as its
/// uniqueness key, so an anonymous logon cannot take a registered name by hiding
/// an invisible character or an extra space in it.
/// </summary>
public sealed class P1024ReservedCallsignSkeletonTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;
        public StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_respond());
    }

    private static HttpClient OkClient(params string[] names) => new(new StubHandler(() =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(names), Encoding.UTF8, "application/json"),
        }))
    { BaseAddress = new Uri("http://lobby.test") };

    [Fact]
    public async Task A_registered_name_is_protected_against_every_lookalike_variant()
    {
        using var cache = new RegisteredCallsignCache(OkClient("Admin"));
        await cache.PumpAsync();

        Assert.True(cache.Contains("Admin"));
        Assert.True(cache.Contains("admin\u00a0"));        // trailing no-break space
        Assert.True(cache.Contains("Ad\u00admin"));        // soft hyphen
        Assert.True(cache.Contains("  ADMIN  "));         // surrounding spaces
        Assert.True(cache.Contains("Admin\u200b"));        // zero-width space
        Assert.False(cache.Contains("Adm in"));            // a different name stays free
        Assert.False(cache.Contains("Administrator"));
        Assert.Equal(new[] { "ADMIN" }, cache.Snapshot);
    }

    [Fact]
    public async Task Whitespace_runs_and_legacy_spellings_keep_their_skeleton_protected()
    {
        using var cache = new RegisteredCallsignCache(OkClient("Admin Smith", "Ad\u00admin"));
        await cache.PumpAsync();

        Assert.True(cache.Contains("Admin  Smith"));       // collapsed to one space
        Assert.True(cache.Contains("admin\tSmith"));       // a tab is a word break
        Assert.True(cache.Contains("Admin"));              // the legacy row still protects its skeleton
        Assert.True(cache.Contains("ad\u00admin"));
        Assert.False(cache.Contains("Admin Smyth"));
        Assert.Equal(2, cache.Snapshot.Count);
    }
}
