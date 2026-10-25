using System.Net.Http.Json;
using Allegiance.Lobby.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P808c — the lobby's registered-callsigns read that game servers poll so an
/// anonymous logon cannot take a registered player's name (reserved-callsign
/// protection). Verifies the endpoint returns exactly the account-held
/// callsigns, and that the store query is plain and case-preserving.
/// </summary>
public sealed class P808cReservedCallsignTests
{
    [Fact]
    public async Task Callsigns_endpoint_returns_registered_account_names_only()
    {
        using var app = new LobbyTestApp();
        using var http = app.CreateClient();
        var store = app.Services.GetRequiredService<IdentityStore>();

        // No accounts yet: empty list, not an error.
        var empty = (await http.GetFromJsonAsync<string[]>("/api/identity/callsigns"))!;
        Assert.Empty(empty);

        var a = store.CreateAccount("AlicePilot");
        var b = store.CreateAccount("BobBomber");
        _ = store.CreateAccount("cEciaL  Case");       // case preserved on the wire

        var names = (await http.GetFromJsonAsync<string[]>("/api/identity/callsigns"))!;
        Assert.Contains(a.Callsign, names);
        Assert.Contains(b.Callsign, names);
        Assert.Contains("cEciaL  Case", names);
        Assert.Equal(3, names.Length);

        // A name with no account is never listed.
        Assert.DoesNotContain("NobodyHere", names);
    }
}
