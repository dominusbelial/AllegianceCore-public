using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1051OnlineDetachmentTests
{
    [Fact]
    public async Task Retiring_session_does_not_retain_the_detached_renderer_through_world_callbacks()
    {
        await using var session = new ConnectionSession();
        var renderer = Detach(session);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(renderer.TryGetTarget(out _));
        // The session/world remains alive to drain its queued inbound packets.
        Assert.False(session.IsDisposed);
        GC.KeepAlive(session.World);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Game1> Detach(ConnectionSession session)
    {
        // Exercise the lifecycle without creating a native window/graphics device.
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Game1).GetField("_mouseFlight", hidden)!.SetValue(game, new MouseFlightController());
        typeof(Game1).GetField("_gamePadFlight", hidden)!.SetValue(game, new JoystickFlightController());
        typeof(Game1).GetField("_flightSession", hidden)!.SetValue(game, session);
        foreach (var (propertyName, methodName) in new[]
        {
            ("RemoteShipHullResolver", "ResolveRemoteHullStats"),
            ("LifepodHullResolver", "ResolveLifepodHull"),
        })
        {
            var property = session.World.GetType().GetProperty(propertyName)!;
            var callback = typeof(Game1).GetMethod(methodName, hidden)!.CreateDelegate(property.PropertyType, game);
            property.SetValue(session.World, callback);
        }
        var reference = new WeakReference<Game1>(game);
        game.AttachOnlineSession(null);
        return reference;
    }
}
