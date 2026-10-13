using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P626 — parity wave 1: in-flight utility pack. Unit tests for the six
/// command families ported with native semantics (chat buffer scroll,
/// vote yes/no, cargo selection cycle, drop-cargo, reload) plus the catalogue
/// registration / help-overlay surfacing of every new command. All behavioural
/// assertions are pure (no graphics device), matching the P624 test file.
/// </summary>
public class P626InFlightUtilityPackTests
{
    // ── chat buffer scroll: ChatScroll (PgUp/PgDn/Prev/NextCommMsg) ────────
    [Theory]
    [InlineData(0, 8, 8, 0)]       // exactly one pane -> no scroll possible
    [InlineData(0, 20, 8, 0)]      // offset 0 stays 0 (max is 12)
    [InlineData(5, 20, 8, 5)]
    [InlineData(50, 20, 8, 12)]    // oversized offset clamps to max
    public void ChatScroll_Clamp_Bounds(int offset, int count, int visible, int expected)
    {
        Assert.Equal(expected, ChatScroll.Clamp(offset, count, visible));
    }

    [Fact]
    public void ChatScroll_PageOlder_And_StepOlder_IncrementTowardHistory()
    {
        Assert.Equal(8, ChatScroll.PageOlder(0, 30, 8));      // one page up
        Assert.Equal(1, ChatScroll.StepOlder(0, 30, 8));      // one line older
        Assert.Equal(20, ChatScroll.PageOlder(12, 30, 8));    // 12 + 8 = 20
    }

    [Fact]
    public void ChatScroll_PageNewer_And_StepNewer_ReturnTowardLiveEdge()
    {
        Assert.Equal(4, ChatScroll.PageNewer(12, 30, 8));     // 12 - 8
        Assert.Equal(11, ChatScroll.StepNewer(12, 30, 8));    // 12 - 1
        Assert.Equal(0, ChatScroll.PageNewer(4, 30, 8));      // clamps at 0
        Assert.Equal(0, ChatScroll.StepNewer(0, 30, 8));      // live edge never goes negative
    }

    [Fact]
    public void ChatScroll_StartIndex_WindowSlidesIntoHistory()
    {
        // count=30, visible=8: offset 0 shows [22..29], offset 12 shows [10..17].
        Assert.Equal(22, ChatScroll.StartIndex(0, 30, 8));
        Assert.Equal(10, ChatScroll.StartIndex(12, 30, 8));
        Assert.Equal(0, ChatScroll.StartIndex(0, 8, 8));       // exactly one pane -> start at oldest (0)
    }

    [Fact]
    public void ChatScroll_CanScroll_FalseWhenItAllFits()
    {
        Assert.False(ChatScroll.CanScroll(8, 8));
        Assert.False(ChatScroll.CanScroll(4, 8));
        Assert.True(ChatScroll.CanScroll(9, 8));
    }

    // ── cargo selection: CargoSelector (NextCargo/NextPart/PrevPart) ────────
    private static Allegiance.Core.Wire.ExpandedPartData Cargo(int mount, int amount = 1)
        => new(PartId: 900, Amount: (short)amount, MountId: unchecked((byte)mount), FractionMounted: 0);

    [Fact]
    public void CargoSelector_EncodesAndDecodesNegativeMounts()
    {
        Assert.True(CargoSelector.IsCargoMount(unchecked((byte)-1)));
        Assert.Equal((sbyte)-1, CargoSelector.SignedMount(unchecked((byte)-1)));
        Assert.Equal(unchecked((byte)-5), CargoSelector.EncodeMount(-5));
        Assert.False(CargoSelector.IsCargoMount(0));
        Assert.Equal((sbyte)0, CargoSelector.SignedMount(0));
    }

    [Fact]
    public void CargoSelector_OccupiedMounts_SortsAscending()
    {
        var mounts = CargoSelector.OccupiedMounts(new[]
        {
            Cargo(-2), Cargo(-1), Cargo(-5),   // shuffled
        });
        Assert.Equal(new[] { (sbyte)-5, (sbyte)-2, (sbyte)-1 }, mounts);
    }

    [Fact]
    public void CargoSelector_Next_CyclesForwardThroughOccupiedMounts()
    {
        // Occupied: -5, -2, -1 (sorted ascending). Native NextCargoPart walks
        // descending mounts (-1 → -2 → ... → -5) wrapping.
        var mounts = new[] { (sbyte)-5, (sbyte)-2, (sbyte)-1 };
        Assert.Equal(-2, CargoSelector.Next(-1, mounts));   // -1 → -2
        Assert.Equal(-5, CargoSelector.Next(-2, mounts));   // -2 → -5
        Assert.Equal(-1, CargoSelector.Next(-5, mounts));   // wrap -5 → -1
    }

    [Fact]
    public void CargoSelector_Prev_CyclesBackward()
    {
        var mounts = new[] { (sbyte)-5, (sbyte)-2, (sbyte)-1 };
        Assert.Equal(-1, CargoSelector.Prev(-2, mounts));   // -2 → -1
        Assert.Equal(-2, CargoSelector.Prev(-5, mounts));
        Assert.Equal(-5, CargoSelector.Prev(-1, mounts));   // wrap
    }

    [Fact]
    public void CargoSelector_EmptyHold_ReturnsNone()
    {
        Assert.Equal(-1, CargoSelector.Next(-1, new sbyte[0]));
        Assert.Equal(-1, CargoSelector.Prev(-1, new sbyte[0]));
        Assert.Equal(-1, CargoSelector.FirstOccupied(new sbyte[0]));
    }

    // ── catalogue: every new command registered with key + native name ─────
    [Theory]
    [InlineData("ChatPageUp", Keys.PageUp, "TK_ChatPageUp")]
    [InlineData("ChatPageDown", Keys.PageDown, "TK_ChatPageDown")]
    [InlineData("PrevCommMsg", Keys.PageUp, "TK_PrevCommMsg")]
    [InlineData("NextCommMsg", Keys.PageDown, "TK_NextCommMsg")]
    [InlineData("NextCargo", Keys.OemComma, "TK_NextCargo")]
    [InlineData("DropCargo", Keys.OemComma, "TK_DropCargo")]
    [InlineData("Reload", Keys.R, "TK_Reload")]
    [InlineData("PrevPart", Keys.None, "TK_PrevPart")]
    [InlineData("NextPart", Keys.None, "TK_NextPart")]
    [InlineData("VoteYes", Keys.Y, "TK_VoteYes")]
    [InlineData("VoteNo", Keys.N, "TK_VoteNo")]
    public void Catalogue_RegistersP626Command_WithKeyAndNativeName(string action, Keys key, string nativeName)
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        var def = map.Definitions.First(d => d.Name == action);
        Assert.Equal(key, map.BindingOf(action).Key);
        Assert.Contains(nativeName, def.Description);   // native TK name in the help text
    }

    [Fact]
    public void Catalogue_NativeWins_KeyChoices()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        // ChatPage owns the native PageUp/PageDown (defaultinputmap.mdl VK 0x21/0x22);
        // CycleCommandee vacated PageDown for it (now Shift+PageDown).
        Assert.Equal(Keys.PageUp, map.BindingOf("ChatPageUp").Key);
        Assert.Equal(Keys.PageDown, map.BindingOf("ChatPageDown").Key);
        Assert.Equal(InputModifiers.Shift, map.BindingOf("CycleCommandee").Modifiers);
        // NextCargo owns the native "," ; DropCargo its native Ctrl+"," .
        Assert.Equal(Keys.OemComma, map.BindingOf("NextCargo").Key);
        Assert.Equal(InputModifiers.None, map.BindingOf("NextCargo").Modifiers);
        Assert.Equal(Keys.OemComma, map.BindingOf("DropCargo").Key);
        Assert.Equal(InputModifiers.Control, map.BindingOf("DropCargo").Modifiers);
        // VoteYes owns the native Y (contextual with ready-check Y on a pre-flight screen).
        Assert.Equal(Keys.Y, map.BindingOf("VoteYes").Key);
        Assert.Equal(InputModifiers.None, map.BindingOf("VoteYes").Modifiers);
        // VoteNo: native N is the port's DropMine (P224); native chaff key Z is
        // taken by enemy-base targeting (P415), so VoteNo rides Ctrl+N (classified
        // divergence). Reload: native R is the port's QuickChat (P217); manual
        // reload rides Shift+R (P192 auto-reload covers empty pools).
        Assert.Equal(Keys.N, map.BindingOf("VoteNo").Key);
        Assert.Equal(InputModifiers.Control, map.BindingOf("VoteNo").Modifiers);
        Assert.Equal(Keys.R, map.BindingOf("Reload").Key);
        Assert.Equal(InputModifiers.Shift, map.BindingOf("Reload").Modifiers);
    }

    [Fact]
    public void Catalogue_NewCommandsAppearInHelpOverlay()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        var all = map.ToHelpGroups().SelectMany(g => g.Entries).ToList();
        foreach (var desc in new[]
        {
            "Rotate the selected cargo item [TK_NextCargo]",
            "Drop the selected cargo item into space [TK_DropCargo]",
            "Reload weapons/afterburner/magazines from cargo [TK_Reload]",
            "Chat pane: page up (older) [TK_ChatPageUp]",
            "Chat pane: page down (newer) [TK_ChatPageDown]",
            "Previous chat message (scroll older) [TK_PrevCommMsg]",
            "Next chat message (scroll newer) [TK_NextCommMsg]",
            "Vote YES on the active ballot [TK_VoteYes]",
            "Vote NO on the active ballot [TK_VoteNo]",
        })
            Assert.Contains(all, e => e.Description == desc);
    }

    [Fact]
    public void Catalogue_P626Commands_NoDuplicateKeysWithinAGroup()
    {
        foreach (var group in new ActionMap(InputActionCatalog.Definitions).ToHelpGroups())
        {
            var dup = group.Entries
                .Select(e => e.Key)
                .GroupBy(k => k)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);
            Assert.Empty(dup);
        }
    }
}
