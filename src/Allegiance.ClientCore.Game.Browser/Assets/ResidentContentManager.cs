using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework.Content;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>KNI's ContentManager normally opens TitleContainer. Browser content always comes from resident verified bytes.</summary>
public sealed class ResidentContentManager(IServiceProvider services, IAssetSource assets) : ContentManager(services)
{
    protected override Stream OpenStream(string assetName) => assets.OpenRead($"{RootDirectory}/{assetName}.xnb");
}
