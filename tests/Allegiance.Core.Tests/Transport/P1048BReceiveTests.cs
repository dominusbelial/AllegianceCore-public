using System.Net;
using System.Net.WebSockets;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class P1048BReceiveTests
{
    [Theory]
    [InlineData("fragments", true)] [InlineData("text", false)] [InlineData("empty-fragments", false)]
    [InlineData("oversize-fragments", false)] [InlineData("close", false)]
    public async Task Binary_fragment_reassembly_delivers_one_packet_and_malformed_messages_lose_once(string mode, bool accepted)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build(); app.UseWebSockets();
        var deliver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] packet = FedPacket.Pack([new PositionReq { SideId = 3 }.Encode()]);
        app.Map("/game", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync(WebSocketTransport.Subprotocol);
            try
            {
                await deliver.Task.WaitAsync(context.RequestAborted);
                if (mode == "fragments")
                {
                    await socket.SendAsync(packet.AsMemory(0, 2), WebSocketMessageType.Binary, false, context.RequestAborted);
                    await socket.SendAsync(packet.AsMemory(2), WebSocketMessageType.Binary, true, context.RequestAborted);
                    // Keep it open until client cleanup; no extra loss before assertion.
                    await socket.ReceiveAsync(new byte[1], context.RequestAborted);
                }
                else if (mode == "text") await socket.SendAsync(packet, WebSocketMessageType.Text, true, context.RequestAborted);
                else if (mode == "empty-fragments")
                    for (int i = 0; i < 257; i++) await socket.SendAsync(Array.Empty<byte>(), WebSocketMessageType.Binary, false, context.RequestAborted);
                else if (mode == "oversize-fragments")
                {
                    await socket.SendAsync(new byte[WebSocketTransport.MessageCap], WebSocketMessageType.Binary, false, context.RequestAborted);
                    await socket.SendAsync(new byte[1], WebSocketMessageType.Binary, false, context.RequestAborted);
                }
                else await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        });
        await app.StartAsync();
        var uri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var site = new P1048BClientWebSocketTests.ClientSite();
        await using var client = new WebSocketTransport(site, WebSocketEndpoint.FromConfiguration($"ws://127.0.0.1:{uri.Port}/game"));
        await client.JoinAsync("", 0, ""); deliver.SetResult();
        await P1048WebSocketTests.Until(() => site.Packets.Count > 0 || site.Lost > 0);
        if (accepted) { Assert.Equal(packet, Assert.Single(site.Packets)); Assert.Equal(0, site.Lost); }
        else { Assert.Empty(site.Packets); Assert.Equal(1, site.Lost); Assert.False(client.IsConnected); }
        await client.DisposeAsync();
        await app.StopAsync();
    }
}
