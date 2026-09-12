namespace Allegiance.Lobby.Tests;

/// <summary>
/// Security check on the static browser page: the served <c>app.js</c> must render
/// server-supplied fields exclusively via safe DOM construction (<c>createElement</c> +
/// <c>textContent</c>) and must NOT use <c>innerHTML</c> with string interpolation of a
/// listing field. A server's <c>Name</c>/<c>MissionName</c>/<c>Address</c> is
/// attacker-controlled; <c>innerHTML</c>-building them would allow script injection into
/// every visitor's browser. This is a content assertion on the served file (not a full
/// browser render), which is enough to catch the regression this page guards against.
/// </summary>
public class BrowserPageTests
{
    [Fact]
    public async Task App_js_renders_via_createElement_and_textContent_only()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var js = await client.GetStringAsync("/app.js");

        // Safe DOM construction is present and used for listing fields.
        Assert.Contains("createElement", js);
        Assert.Contains(".textContent", js);
        Assert.DoesNotContain("innerHTML", js);
    }

    [Fact]
    public async Task Index_html_and_app_js_are_served()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var html = await client.GetAsync("/");
        Assert.True(html.IsSuccessStatusCode);
        var body = await html.Content.ReadAsStringAsync();
        Assert.Contains("servers", body);

        var js = await client.GetAsync("/app.js");
        Assert.True(js.IsSuccessStatusCode);
    }
}
