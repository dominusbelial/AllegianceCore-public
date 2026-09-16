using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P234 — resolves which real <c>.mdl</c> missile model the renderer loads.
/// This port's MonoGame client is a renderer with <b>no static core</b>, so it
/// cannot map the wire <c>typeId</c> to a model name the way the server (with
/// <c>DataMissileTypeIGC.ModelName</c>) can. The task scopes the renderer to
/// "load <b>one</b> missile type's real parsed mesh once", so this stage picks
/// the real mesh for the single demo missile type the live recipe fires — the
/// first <c>MaxLock==0</c> type in the PCore001 core, which is ExpendableTypeId
/// 34, model <c>mis05</c> (verified by dumping the core; <c>mis05.mdl</c> ships
/// in the artwork). Every in-flight missile renders with this one mesh; a
/// per-type model map fed from the static core is a future task.
/// </summary>
public static class MissileModelSource
{
    /// <summary>The real <c>.mdl</c> stem for the P234 demo missile type (PCore001
    /// type 34). Kept as the single-model scope; see the class doc.</summary>
    public const string DemoModelStem = "mis05";

    /// <summary>The configured artwork root (ALLEGIANCE_ARTWORK_PATH, falling
    /// back to the standard dev location), where <c>.mdl</c> assets live.</summary>
    public static string ResolveArtworkDir() =>
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    /// <summary>Parse the demo missile's real <c>.mdl</c> once. Throws if the
    /// artwork path is unavailable — the model is required for rendering.</summary>
    public static MdlFile LoadDemoModel() =>
        MdlFile.Parse(System.IO.Path.Combine(ResolveArtworkDir(), DemoModelStem + ".mdl"));
}
