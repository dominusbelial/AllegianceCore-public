namespace Allegiance.Core.Model;

/// <summary>
/// The fixed per-side-id colour palette native uses to tint each side.
/// Native assigns a side its colour purely by <b>side id</b> (side slot), not
/// by civilization, inside the mission-setup <c>UpdateSides</c> pass
/// (<c>missionigc.cpp:3734-3746</c>):
/// <code>
///   static const float sideColors[c_cSidesMax][3] =
///     { {188,160,  0},   // Gold        (side 0)
///       {  0,138,217},   // Blue        (side 1)
///       {156, 16,102},   // Purple      (side 2)
///       { 50,140, 20},   // icky yellow (side 3)
///       {255,145,145},   // icky orange (side 4)
///       { 50,200,125}};  // icky magenta (side 5)
///   ... ds.color.SetRGBA(sideColors[sid][0], sideColors[sid][1],
///                        sideColors[sid][2]);   // all /255, alpha default 1
/// </code>
/// <see cref="Side.ColorR"/>/<see cref="Side.ColorG"/>/<see cref="Side.ColorB"/>
/// are seeded from this in <see cref="Mission.Assemble"/>. Side ids outside
/// 0..5 (c_cSidesMax = 6) cannot occur in a real mission but fall back to the
/// neutral gray native uses for an absent side (<c>Color(0.5,0.5,0.5)</c>,
/// fsmission.cpp:3073).
/// </summary>
public static class SidePalette
{
    private static readonly (float R, float G, float B)[] Colors =
    [
        (188f / 255f, 160f / 255f, 0f / 255f),          // Gold
        (0f / 255f, 138f / 255f, 217f / 255f),          // Blue
        (156f / 255f, 16f / 255f, 102f / 255f),         // Purple
        (50f / 255f, 140f / 255f, 20f / 255f),          // icky yellow
        (255f / 255f, 145f / 255f, 145f / 255f),        // icky orange
        (50f / 255f, 200f / 255f, 125f / 255f),         // icky magenta
    ];

    /// <summary>Resolve a side id to its native palette colour (RGB floats in
    /// 0..1; alpha is always 1). Falls back to the neutral gray (0.5,0.5,0.5)
    /// for an id outside the six-slot palette.</summary>
    public static (float R, float G, float B) ColorFor(short sideId)
    {
        if (sideId >= 0 && sideId < Colors.Length)
            return Colors[sideId];
        return (0.5f, 0.5f, 0.5f);
    }
}
