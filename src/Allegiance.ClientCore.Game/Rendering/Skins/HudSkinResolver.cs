namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>Provides access to the default, lazily-constructed skin catalog.
/// The catalog is shared by <see cref="HudSettings"/> migration and by the
/// game client's startup path.</summary>
public static class HudSkinResolver
{
    private static readonly Lazy<HudSkinCatalog> _catalog = new(() => new HudSkinCatalog());

    public static HudSkinCatalog Catalog => _catalog.Value;
}
