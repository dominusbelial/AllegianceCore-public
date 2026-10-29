using System.Net;
using System.Text;
using Allegiance.ClientCore.MonoGame.Games;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P883bGameCapacityTests
{
    private sealed class CatalogHandler(int current) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {"servers":[{"id":"host","name":"Host","address":"127.0.0.1","port":6073,
                    "core":"PCore001.igc","missionName":"Configured","currentGames":{{current}},"maxGames":2}],
                    "cores":["PCore001.igc"],"maps":["m2xspiderw.igc"]}
                    """, Encoding.UTF8, "application/json"),
            });
    }

    [Theory]
    [InlineData(HudStyle.Classic, 1)]
    [InlineData(HudStyle.Classic, 2)]
    [InlineData(HudStyle.Classic, 3)]
    [InlineData(HudStyle.Modern, 1)]
    [InlineData(HudStyle.Modern, 2)]
    [InlineData(HudStyle.Modern, 3)]
    public async Task Catalog_capacity_drives_shared_wizard_refusal_and_cur_max_text(HudStyle style, int current)
    {
        _ = new HudSettings { Style = style }; // the intro wizard shares one model across both styles
        using var http = new HttpClient(new CatalogHandler(current));
        var client = new LobbyGameClient(http, "http://127.0.0.1");
        var catalog = await client.GetCatalogAsync();
        Assert.NotNull(catalog);
        var wizard = new CreateGameWizard();
        wizard.ApplyCatalog(catalog);
        wizard.TypeName("Capacity game");
        Assert.Equal($"Host ({current}/2)", wizard.SelectedServer!.DisplayName);
        Assert.Equal(current >= 2, wizard.SelectedServer.IsFull);
        if (current >= 2)
        {
            Assert.Null(wizard.TryCreate());
            Assert.Contains("full", wizard.Status);
        }
        else Assert.NotNull(wizard.TryCreate());
    }
}
