using System.Reflection;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// P773 — registry guard for the retired port-only fmid 212
/// (<c>OWN_SHIP_KILL_BONUS_UPDATE</c>): the own kill bonus is derived
/// client-side from the own ship's experience (native
/// <c>WinTrek/console.cpp:824-828</c>, seeded by PLAYERINFO.fExperience
/// <c>appmsg.cpp:2280</c> and updated by KILL_SHIP
/// <c>appmsg.cpp:2769,2807-2811</c>), so no wire type may re-register 212 and
/// the server may not keep the per-tick broadcast machinery.
/// </summary>
public class P773Fmid212RetiredTests
{
    /// <summary>⭐ No type in the wire assembly declares a <c>const ushort
    /// Fmid</c> equal to 212 — the codec and its fmid registration are gone.</summary>
    [Fact]
    public void No_wire_type_declares_fmid_212()
    {
        var offenders =
            typeof(IFedMessage).Assembly.GetTypes()
                .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .Where(f => f.IsLiteral && f.FieldType == typeof(ushort) && f.Name == "Fmid")
                .Where(f => (ushort)f.GetRawConstantValue()! == 212)
                .Select(f => f.DeclaringType!.FullName)
                .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>⭐ The server no longer has the per-tick Kill Bonus broadcast:
    /// neither the step's method nor its pooled packer survives.</summary>
    [Fact]
    public void Server_has_no_kill_bonus_broadcast_members()
    {
        var type = typeof(global::Allegiance.Server.Server);
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        Assert.Null(type.GetMethod("BroadcastOwnShipKillBonus", All));
        Assert.Null(type.GetMethod("PackKillBonus", All));
    }
}
