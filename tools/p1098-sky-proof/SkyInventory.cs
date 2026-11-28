using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

internal static class SkyInventory
{
    internal static object[] Owners(Game1 game) =>
        new[] { "_blackSky", "_nativeSky", "_hdSky", "_starFar", "_starMid", "_starNear", "_alephGlimpseRenderer" }
            .Select(name => Program.Field<object>(game, name)).ToArray();

    // Follow only sky-owned fields, collection values and tuples. Stop at GPU
    // handles, so a device's global registry cannot mask a missing owner Dispose.
    internal static HashSet<GraphicsResource> Resources(object[] owners)
    {
        var found = new HashSet<GraphicsResource>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Visit(object? node)
        {
            if (node is null || !seen.Add(node)) return;
            if (node is GraphicsResource resource) { if (!resource.IsDisposed) found.Add(resource); return; }
            if (node is IDictionary map) { foreach (var value in map.Values) Visit(value); return; }
            if (node is ITuple tuple) { for (int i = 0; i < tuple.Length; i++) Visit(tuple[i]); return; }
            if (node is Array array && array.GetType().GetElementType()!.IsValueType) return;
            if (node is IList list) { foreach (var item in list) Visit(item); return; }
            if (node.GetType().Namespace != typeof(PlanetPoster).Namespace) return;
            foreach (var field in node.GetType().GetFields(Program.Hidden | BindingFlags.Public))
                Visit(field.GetValue(node));
        }
        foreach (var owner in owners) Visit(owner);
        return found;
    }

    internal static void Exercise(Game1 game)
    {
        var sector = Program.Sector(1);
        var view = Matrix.CreateLookAt(Vector3.Zero, Vector3.UnitX, Vector3.Up);
        var projection = Matrix.CreatePerspectiveFieldOfView(.9f, 1.5f, 1, 10000);
        foreach (var sky in Owners(game).OfType<ISkyRenderer>())
        {
            sky.SetCluster(sector);
            sky.Draw(view, projection, Vector3.Zero);
        }
    }

    internal static void Lifetime(Game1 game, bool measure)
    {
        var owners = Owners(game);
        var held = Resources(owners);
        int loaded = held.Count;
        Exercise(game);
        held.UnionWith(Resources(owners));
        var glimpse = owners.OfType<AlephGlimpseRenderer>().Single();
        Program.Check(glimpse.Warm(2, Program.Sector(2), Program.Sector(1), Vector3.UnitX) == 1, "cold destination did not render");
        held.UnionWith(Resources(owners));
        int warmed = held.Count(r => !r.IsDisposed);
        var firstTarget = held.OfType<RenderTarget2D>().Single();
        var beforeReset = new Color[firstTarget.Width * firstTarget.Height];
        firstTarget.GetData(beforeReset);
        game.GraphicsDevice.Reset(game.GraphicsDevice.PresentationParameters);
        int prepared = glimpse.Warm(2, Program.Sector(2), Program.Sector(1), Vector3.UnitX);
        var afterReset = new Color[beforeReset.Length];
        var replacement = Resources(owners).OfType<RenderTarget2D>().Single();
        replacement.GetData(afterReset);
        held.UnionWith(Resources(owners));
        game.Exit();
        game.Tick(); // Game calls UnloadContent while the device is still usable.
        Program.Check(!game.GraphicsDevice.IsDisposed, "device teardown masked owner disposal");
        int live = held.Count(r => !r.IsDisposed);
        Console.WriteLine($"LIFETIME loaded={loaded} warmed={warmed} held={held.Count} liveAfterUnload={live} resetPrepared={prepared}");
        if (!measure)
        {
            Program.Check(live == 0, $"{live} sky resources survived Game1.UnloadContent");
            Program.Check(firstTarget.IsDisposed && !ReferenceEquals(firstTarget, replacement) && prepared == 1, "same-size reset kept a stale postcard");
            Program.Check(beforeReset.SequenceEqual(afterReset), "reset changed the destination sky");
            CheckStarTextureOwner(game);
        }
    }

    // MonoGame suppresses the second Disposing event on a repeated Dispose.
    // Count calls on the HD cache entry itself to expose an extra release via its alias.
    private sealed class CountedTexture(GraphicsDevice device) : Texture2D(device, 1, 1)
    {
        public int Releases { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Releases++;
            base.Dispose(disposing);
        }
    }

    private static void CheckStarTextureOwner(Game1 game)
    {
        var hd = new HdSkyRenderer(game.GraphicsDevice,
            Program.Field<Allegiance.ClientCore.MonoGame.Assets.IAssetSource>(game, "_assets"));
        var cache = Program.Field<Dictionary<string, Texture2D>>(hd, "_textures");
        var star = Program.Field<Texture2D>(hd, "_starTexture");
        string key = cache.Single(entry => ReferenceEquals(entry.Value, star)).Key;
        var counted = new CountedTexture(game.GraphicsDevice);
        cache[key] = counted;
        typeof(HdSkyRenderer).GetField("_starTexture", Program.Hidden)!.SetValue(hd, counted);
        star.Dispose();
        hd.Dispose();
        Program.Check(counted.Releases == 1, $"HD star texture received {counted.Releases} releases");
    }
}
