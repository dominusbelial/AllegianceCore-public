using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class P641FlightPartsTests
{
    private static DataHullTypeIGC Hull => default(DataHullTypeIGC) with
    { MaxWeapons = 3, MaxFixedWeapons = 2, Hardpoints = [new(0, 0, "", "", 1, true), new(0, 0, "", "", 2, true), new(0, 0, "", "", 1, false)], PmEquipment = [3, 3, 3, 3, 3, 3, 3, 3] };
    private static DataPartTypeIGC? Resolve(short id) => default(DataPartTypeIGC) with
    { PartId = id, PartMask = 1, Type = id == 20 ? new DataPartTypeIGC.Afterburner(0, 0, 0, 0, 0, 0) : new DataPartTypeIGC.Weapon(0, 0, 0, 0, 0, 0, 0, 0, 0) };

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Each_weapon_key_selects_single_then_toggles_back_to_group(int mount)
    {
        var single = FlightInput.ToggleWeapon(true, 0, mount, 4, true);
        Assert.False(single.GroupFire);
        Assert.Equal(mount, single.SelectedMount);
        Assert.Equal(ShipControlState.OneWeapon | mount, FlightInput.FireContribution(true, single.GroupFire, single.SelectedMount));
        Assert.Equal((true, mount), FlightInput.ToggleWeapon(false, mount, mount, 4, true));
    }

    [Theory]
    [InlineData(2, 2, true)] [InlineData(0, 2, false)] [InlineData(-1, 2, true)]
    public void Empty_or_nonfixed_mount_is_ignored(int requested, int count, bool occupied)
        => Assert.Equal((true, 1), FlightInput.ToggleWeapon(true, 1, requested, count, occupied));

    [Fact]
    public void Changing_single_weapon_keeps_single_fire()
        => Assert.Equal((false, 1), FlightInput.ToggleWeapon(false, 0, 1, 2, true));

    [Fact]
    public void Mount_uses_individual_socket_mask_and_empty_slot()
    {
        ExpandedPartData cargo = new(10, 1, 255, 255);
        var plan = InFlightPartRules.Plan(FlightPartAction.Mount, cargo, [new(11, 1, 0, 255), cargo], Hull, Resolve, 1);
        Assert.NotNull(plan);
        Assert.Equal(2, plan.MountNew); // mount 1 rejects mask; 0 occupied; 2 is compatible turret
        Assert.False(InFlightPartRules.CanMount(Hull, Resolve(10)!.Value, 1));
        Assert.Null(InFlightPartRules.Plan(FlightPartAction.Mount, cargo, [new(11, 1, 0, 255), new(12, 1, 2, 255), cargo], Hull, Resolve));
    }

    [Fact]
    public void Replace_exchanges_selected_cargo_with_occupied_compatible_equipment()
    {
        ExpandedPartData cargo = new(10, 8, 255, 255);
        var plan = InFlightPartRules.Plan(FlightPartAction.Replace, cargo, [new(11, 1, 0, 255), cargo], Hull, Resolve);
        Assert.Equal(-1, plan!.MountOld);
        Assert.Equal(0, plan.MountNew);
        Assert.Null(InFlightPartRules.Plan(FlightPartAction.Replace, cargo, [cargo], Hull, Resolve));
    }

    [Fact]
    public void Unmount_needs_free_cargo_and_mount_requires_cargo_selection()
    {
        ExpandedPartData equipped = new(20, 1, 0, 255);
        var inventory = new List<ExpandedPartData> { equipped };
        for (int i = 1; i <= 5; i++) inventory.Add(new(10, 1, unchecked((byte)-i), 0));
        Assert.Null(InFlightPartRules.Plan(FlightPartAction.Unmount, equipped, inventory, Hull, Resolve));
        inventory.RemoveAt(2);
        Assert.Equal(-2, InFlightPartRules.Plan(FlightPartAction.Unmount, equipped, inventory, Hull, Resolve)!.MountNew);
        Assert.Null(InFlightPartRules.Plan(FlightPartAction.Mount, equipped, inventory, Hull, Resolve));
        Assert.False(InFlightPartRules.CanMount(Hull, Resolve(20)!.Value, 1));
        Assert.False(InFlightPartRules.CanMount(Hull, Resolve(20)!.Value, -6));
    }

    [Theory]
    [InlineData("ToggleWeapon1")] [InlineData("ToggleWeapon2")] [InlineData("ToggleWeapon3")] [InlineData("ToggleWeapon4")]
    [InlineData("ToggleBooster")] [InlineData("MountPart")] [InlineData("UnmountPart")] [InlineData("ReplacePart")] [InlineData("DropPart")]
    public void New_commands_have_native_identity_help_and_rebindable_actions(string action)
    {
        var entry = InputActionCatalog.Definitions.Single(a => a.Name == action);
        Assert.Contains("TK_" + action, entry.Description);
        var map = new ActionMap(InputActionCatalog.Definitions);
        map.SetBinding(action, new KeyBinding(Keys.F11, InputModifiers.Control | InputModifiers.Shift));
        Assert.True(map.IsDownMostSpecific(new KeyboardState(Keys.F11, Keys.LeftControl, Keys.LeftShift), action));
    }

    [Fact]
    public void Native_defaults_are_1_to_4_and_proof_flag_is_parsed()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        for (int i = 1; i <= 4; i++) Assert.True(map.IsDownMostSpecific(new KeyboardState((Keys)((int)Keys.D0 + i)), "ToggleWeapon" + i));
        Assert.True(AutoProofPlan.Parse(["--auto-flight-parts"], out _).AutoFlightParts);
    }
}
