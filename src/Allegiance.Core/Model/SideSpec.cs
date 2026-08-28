namespace Allegiance.Core.Model;

/// <summary>
/// A <b>caller-supplied side specification</b> for
/// <see cref="Mission.Assemble"/>. The reference engine synthesises each side
/// at mission-setup time from <c>MissionParams::nTeams</c> + <c>rgCivID[]</c>,
/// plus a caller-supplied per-team name array (<c>UpdateSides</c>,
/// <c>missionigc.cpp:3734</c>) — none of which is derivable from a
/// <see cref="StaticCore"/> or <see cref="MissionMap"/> alone. So the static
/// assembler does not invent sides; it consumes them. The civilization is
/// looked up in the core at assemble time and becomes the resolved
/// <see cref="Side.Civilization"/>.
/// </summary>
/// <param name="Id">Side id (<c>sideID</c>), conventionally 0..teams−1.</param>
/// <param name="Name">Side name if the host has one; may be <c>null</c>.</param>
/// <param name="CivilizationId">
/// A civilization id present in the core (the <c>rgCivID[sid]</c> choice).
/// </param>
public readonly record struct SideSpec(short Id, string? Name, short CivilizationId);
