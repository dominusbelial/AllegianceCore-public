using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>The objective shares the player's existing bracket and bearing renderer.</summary>
public sealed record LastBaseHudView(string Text, TargetSelectionView? Bracket, Vector2? Bearing, string BearingLabel);
