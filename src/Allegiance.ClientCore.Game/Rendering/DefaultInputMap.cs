using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P625 - parser for native's <c>defaultinputmap.mdl</c>. Native loads it via
/// its MDL/modeler namespace system: the file declares a <c>keyCommandMap</c>
/// list of (vk, modifiers, TK) triples (WinTrek/trekinput.cpp:484-598) plus
/// mouse/joystick maps. This class transcribes that file into
/// <see cref="NativeBinding"/> records so the port can (a) prove its defaults
/// match native and (b) offer native bindings on the rebind popup.
///
/// Modifier bits follow native (trekinput.cpp:24-29): None=0, Shift=1,
/// Control=2, Alt=4, Any=8. The port treats "Any" as an unmodified chord
/// because MonoGame reads each chord explicitly (matching native's "just
/// shift/control/alt" semantics).
/// </summary>
public static partial class DefaultInputMap
{
    /// <summary>One parsed native binding: Windows virtual-key code, modifier
    /// bits, and the TK command number + name.</summary>
    public readonly record struct NativeBinding(
        int Vk, InputModifiers Modifiers, int TkNumber, string TkName);

    /// <summary>The full parsed <c>keyCommandMap</c> (keyboard only).</summary>
    public static IReadOnlyList<NativeBinding> ParseKeyMap(string mdlText)
    {
        var result = new List<NativeBinding>();
        string? keyBlock = SplitSection(mdlText, "keyCommandMap");
        if (keyBlock == null) return result;

        foreach (var line in keyBlock.Split('\n'))
        {
            int vk, mods, tk;
            if (TryParseTriple(line, out vk, out mods, out tk))
                result.Add(new NativeBinding(vk, (InputModifiers)(mods & ~8), tk, TkName(tk)));
        }
        return result;
    }

    /// <summary>Parsed <c>mouseButtonCommandMap</c> pairs: (buttonIndex, TK).
    /// Buttons per native LoadMap (trekinput.cpp:616-645): 0=left,1=right,
    /// 2=middle, 8=wheel-down, 9=wheel-up.</summary>
    public static IReadOnlyList<(int Button, int Tk)> ParseMouseMap(string mdlText)
    {
        var result = new List<(int, int)>();
        string? block = SplitSection(mdlText, "mouseButtonCommandMap");
        if (block == null) return result;
        foreach (var line in block.Split('\n'))
        {
            int a, b;
            if (TryParsePair(line, out a, out b))
                result.Add((a, b));
        }
        return result;
    }

    private static string? SplitSection(string mdlText, string section)
    {
        // Everything from '<section> =' up to the next top-level ';' (native
        // MDL assignment list). We scan for the section marker then the list.
        int idx = mdlText.IndexOf(section, StringComparison.Ordinal);
        if (idx < 0) return null;
        int semi = mdlText.IndexOf(';', idx);
        if (semi < 0) return mdlText.Substring(idx);
        return mdlText.Substring(idx, semi - idx);
    }

    private static bool TryParseTriple(string line, out int a, out int b, out int c)
    {
        a = b = c = 0;
        var nums = ExtractNumbers(line);
        if (nums.Count < 3) return false;
        a = nums[0]; b = nums[1]; c = nums[2];
        return true;
    }

    private static bool TryParsePair(string line, out int a, out int b)
    {
        a = b = 0;
        var nums = ExtractNumbers(line);
        if (nums.Count < 2) return false;
        a = nums[0]; b = nums[1];
        return true;
    }

    private static List<int> ExtractNumbers(string line)
    {
        var list = new List<int>();
        foreach (var tok in line.Split(',', '(', ')'))
        {
            var t = tok.Trim();
            if (t.Length == 0) continue;
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                list.Add(n);
        }
        return list;
    }

    /// <summary>Windows VK code -> MonoGame <see cref="Keys"/>, transcribed from
    /// <c>keycode.mdl</c> (VK constants) plus the implicit A-Z/0-9 ranges.</summary>
    public static Keys? VkToKey(int vk) => vk switch
    {
        0x08 => Keys.Back, 0x09 => Keys.Tab, 0x0D => Keys.Enter, 0x10 => Keys.LeftShift,
        0x11 => Keys.LeftControl, 0x12 => Keys.LeftAlt, 0x13 => Keys.Pause, 0x14 => Keys.CapsLock,
        0x1B => Keys.Escape, 0x20 => Keys.Space, 0x21 => Keys.PageUp, 0x22 => Keys.PageDown,
        0x23 => Keys.End, 0x24 => Keys.Home, 0x25 => Keys.Left, 0x26 => Keys.Up,
        0x27 => Keys.Right, 0x28 => Keys.Down, 0x2C => Keys.PrintScreen, 0x2D => Keys.Insert,
        0x2E => Keys.Delete,
        0x60 => Keys.NumPad0, 0x61 => Keys.NumPad1, 0x62 => Keys.NumPad2, 0x63 => Keys.NumPad3,
        0x64 => Keys.NumPad4, 0x65 => Keys.NumPad5, 0x66 => Keys.NumPad6, 0x67 => Keys.NumPad7,
        0x68 => Keys.NumPad8, 0x69 => Keys.NumPad9, 0x6B => Keys.Add, 0x6D => Keys.Subtract,
        0x6E => Keys.OemPeriod, 0x6F => Keys.OemQuestion,
        0x70 => Keys.F1, 0x71 => Keys.F2, 0x72 => Keys.F3, 0x73 => Keys.F4, 0x74 => Keys.F5,
        0x75 => Keys.F6, 0x76 => Keys.F7, 0x77 => Keys.F8, 0x78 => Keys.F9, 0x79 => Keys.F10,
        0x7A => Keys.F11, 0x7B => Keys.F12,
        0xBA => Keys.OemSemicolon, 0xBB => Keys.OemPlus, 0xBC => Keys.OemComma,
        0xBD => Keys.OemMinus, 0xBE => Keys.OemPeriod, 0xBF => Keys.OemQuestion,
        0xC0 => Keys.OemTilde, 0xDB => Keys.OemOpenBrackets, 0xDC => Keys.OemBackslash,
        0xDD => Keys.OemCloseBrackets, 0xDE => Keys.OemQuotes,
        _ when vk >= 0x30 && vk <= 0x39 => (Keys)(Keys.D0 + (vk - 0x30)),
        _ when vk >= 0x41 && vk <= 0x5A => (Keys)(Keys.A + (vk - 0x41)),
        _ => null,
    };

    /// <summary>Resolve a parsed <see cref="NativeBinding"/> to a MonoGame chord
    /// (null when native used a key with no MonoGame equivalent, e.g. NumLock).</summary>
    public static KeyBinding? ToChord(NativeBinding nb)
    {
        var key = VkToKey(nb.Vk);
        return key is { } k ? new KeyBinding(k, nb.Modifiers) : null;
    }

    /// <summary>The native TK command name for a TK number, decoded from the
    /// TrekKey enum (Igc/igc.h:226-451). Unknown numbers fall back to
    /// "TK_&lt;n&gt;".</summary>
    public static string TkName(int tk) =>
        _tkNames.TryGetValue(tk, out var name) ? name : $"TK_{tk}";

    public static int? TkNumber(string name) => _tkNames
        .Where(kv => string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase))
        .Select(kv => (int?)kv.Key).FirstOrDefault();

    // Transcribed from Igc/igc.h:226-451 (the commands the port implements are
    // the ones surfaced in InputActionCatalog; this table names any that appear).
    private static readonly Dictionary<int, string> _tkNames = new()
    {
        { 0,"TK_NextCommMsg" },{ 1,"TK_PrevCommMsg" },{ 2,"TK_PrevWeapon" },{ 3,"TK_NextWeapon" },
        { 4,"TK_ToggleGroupFire" },{ 5,"TK_PrevPart" },{ 6,"TK_NextPart" },{ 7,"TK_MountPart" },
        { 8,"TK_ReplacePart" },{ 9,"TK_UnmountPart" },{ 10,"TK_DropPart" },{ 11,"TK_ZoomOut" },
        { 12,"TK_ZoomIn" },{ 17,"TK_PitchUp" },{ 18,"TK_PitchDown" },{ 19,"TK_YawLeft" },
        { 20,"TK_YawRight" },{ 21,"TK_RollLeft" },{ 22,"TK_RollRight" },{ 23,"TK_FOVOut" },
        { 24,"TK_FOVIn" },{ 25,"TK_MainMenu" },{ 26,"TK_ViewFrontLeft" },{ 27,"TK_ViewFrontRight" },
        { 28,"TK_ViewLeft" },{ 29,"TK_ViewRight" },{ 30,"TK_ViewExternal" },{ 31,"TK_ViewFlyby" },
        { 32,"TK_ViewMissile" },{ 33,"TK_ViewTarget" },{ 34,"TK_ViewChase" },{ 35,"TK_ViewTurret" },
        { 36,"TK_ViewBase" },{ 37,"TK_ViewSector" },{ 38,"TK_ViewCommand" },{ 39,"TK_CycleChatMsgs" },
        { 40,"TK_AcceptCommand" },{ 41,"TK_ClearCommand" },{ 42,"TK_RejectCommand" },
        { 43,"TK_ToggleAutoPilot" },{ 44,"TK_ThrustLeft" },{ 45,"TK_ThrustRight" },
        { 46,"TK_ThrustUp" },{ 47,"TK_ThrustDown" },{ 48,"TK_ThrustForward" },
        { 49,"TK_ThrustBackward" },{ 50,"TK_FireBooster" },{ 51,"TK_ConModeGameState" },
        { 52,"TK_QuickChatMenu" },{ 53,"TK_ToggleLODSlider" },{ 54,"TK_StartChat" },
        { 55,"TK_FireWeapon" },{ 56,"TK_FireMissile" },{ 57,"TK_LockVector" },
        { 58,"TK_ThrottleUp" },{ 59,"TK_ThrottleDown" },{ 60,"TK_ThrottleZero" },
        { 61,"TK_Throttle33" },{ 62,"TK_Throttle66" },{ 63,"TK_ThrottleFull" },
        { 64,"TK_ToggleBooster" },{ 65,"TK_LODUp" },{ 66,"TK_LODDown" },{ 67,"TK_ConModeCombat" },
        { 68,"TK_ConModeCommand" },{ 69,"TK_ConModeNav" },{ 70,"TK_ConModeInventory" },
        { 71,"TK_ConModeInvest" },{ 72,"TK_ConModeTeam" },{ 73,"TK_ViewRear" },{ 74,"TK_Target" },
        { 75,"TK_TargetNearest" },{ 76,"TK_TargetPrev" },{ 77,"TK_TargetFriendly" },
        { 78,"TK_TargetFriendlyNearest" },{ 79,"TK_TargetFriendlyPrev" },{ 80,"TK_TargetEnemy" },
        { 81,"TK_TargetEnemyNearest" },{ 82,"TK_TargetEnemyPrev" },{ 83,"TK_TargetEnemyBase" },
        { 84,"TK_TargetEnemyBaseNearest" },{ 85,"TK_TargetEnemyBasePrev" },
        { 86,"TK_TargetFriendlyBase" },{ 87,"TK_TargetFriendlyBaseNearest" },
        { 88,"TK_TargetFriendlyBasePrev" },{ 89,"TK_TargetCommand" },{ 90,"TK_TargetCenter" },
        { 91,"TK_TargetHostile" },{ 92,"TK_Suicide" },{ 93,"TK_ToggleGrid" },
        { 94,"TK_ToggleCloak" },{ 95,"TK_DropMine" },{ 96,"TK_Ripcord" },{ 97,"TK_ViewRearLeft" },
        { 98,"TK_ViewRearRight" },{ 100,"TK_TargetSelf" },{ 101,"TK_ToggleCollisions" },
        { 102,"TK_OccupyNextTurret" },{ 103,"TK_TargetNothing" },{ 104,"TK_MatchSpeed" },
        { 105,"TK_ChatPageUp" },{ 106,"TK_ChatPageDown" },{ 107,"TK_ViewExternalOrbit" },
        { 108,"TK_ViewExternalStation" },{ 109,"TK_QuickChat1" },{ 110,"TK_QuickChat2" },
        { 111,"TK_QuickChat3" },{ 112,"TK_QuickChat4" },{ 113,"TK_QuickChat5" },
        { 114,"TK_QuickChat6" },{ 115,"TK_QuickChat7" },{ 116,"TK_QuickChat8" },
        { 117,"TK_QuickChat9" },{ 118,"TK_QuickChat0" },{ 119,"TK_DebugTest1" },
        { 120,"TK_DebugTest2" },{ 121,"TK_DebugTest3" },{ 122,"TK_DebugTest4" },
        { 123,"TK_DebugTest5" },{ 124,"TK_DebugTest6" },{ 125,"TK_DebugTest7" },
        { 126,"TK_DebugTest8" },{ 127,"TK_DebugTest9" },{ 128,"TK_DropChaff" },
        { 129,"TK_ToggleCommand" },{ 130,"TK_SwapWeapon1" },{ 131,"TK_SwapWeapon2" },
        { 132,"TK_SwapWeapon3" },{ 133,"TK_SwapWeapon4" },{ 134,"TK_SwapTurret1" },
        { 135,"TK_SwapTurret2" },{ 136,"TK_SwapTurret3" },{ 137,"TK_SwapTurret4" },
        { 138,"TK_SwapMissile" },{ 139,"TK_SwapChaff" },{ 140,"TK_SwapShield" },
        { 141,"TK_SwapCloak" },{ 142,"TK_SwapAfterburner" },{ 143,"TK_SwapMine" },
        { 144,"TK_NextCargo" },{ 145,"TK_ToggleWeapon1" },{ 146,"TK_ToggleWeapon2" },
        { 147,"TK_ToggleWeapon3" },{ 148,"TK_ToggleWeapon4" },{ 154,"TK_DropCargo" },
        { 155,"TK_Reload" },{ 156,"TK_ExecuteCommand" },{ 157,"TK_PauseTM" },
        { 158,"TK_TargetEnemyBomber" },{ 159,"TK_TargetEnemyFighter" },
        { 160,"TK_TargetEnemyConstructor" },{ 161,"TK_TargetEnemyMiner" },
        { 162,"TK_TargetEnemyMajorBase" },{ 163,"TK_TargetFriendlyBomber" },
        { 164,"TK_TargetFriendlyFighter" },{ 165,"TK_TargetFriendlyConstructor" },
        { 166,"TK_TargetFriendlyMiner" },{ 167,"TK_TargetFriendlyLifepod" },
        { 168,"TK_TargetFriendlyMajorBase" },{ 169,"TK_TargetFlag" },{ 170,"TK_TargetArtifact" },
        { 171,"TK_TargetEnemyBomberNearest" },{ 172,"TK_TargetEnemyFighterNearest" },
        { 173,"TK_TargetEnemyConstructorNearest" },{ 174,"TK_TargetEnemyMinerNearest" },
        { 175,"TK_TargetEnemyMajorBaseNearest" },{ 176,"TK_TargetFriendlyBomberNearest" },
        { 177,"TK_TargetFriendlyFighterNearest" },
        { 178,"TK_TargetFriendlyConstructorNearest" },{ 179,"TK_TargetFriendlyMinerNearest" },
        { 180,"TK_TargetFriendlyLifepodNearest" },
        { 181,"TK_TargetFriendlyMajorBaseNearest" },{ 182,"TK_TargetFlagNearest" },
        { 183,"TK_TargetArtifactNearest" },{ 184,"TK_TargetEnemyBomberPrev" },
        { 185,"TK_TargetEnemyFighterPrev" },{ 186,"TK_TargetEnemyConstructorPrev" },
        { 187,"TK_TargetEnemyMinerPrev" },{ 188,"TK_TargetEnemyMajorBasePrev" },
        { 189,"TK_TargetFriendlyBomberPrev" },{ 190,"TK_TargetFriendlyFighterPrev" },
        { 191,"TK_TargetFriendlyConstructorPrev" },{ 192,"TK_TargetFriendlyMinerPrev" },
        { 193,"TK_TargetFriendlyLifepodPrev" },{ 194,"TK_TargetFriendlyMajorBasePrev" },
        { 195,"TK_TargetFlagPrev" },{ 196,"TK_TargetArtifactPrev" },{ 197,"TK_TargetWarp" },
        { 198,"TK_TargetWarpNearest" },{ 199,"TK_TargetWarpPrev" },{ 200,"TK_SkipTMGoal" },
        { 201,"TK_IncreaseTMClockSpeed" },{ 202,"TK_DecreaseTMClockSpeed" },
        { 203,"TK_ResetTMClockSpeed" },{ 204,"TK_Help" },{ 205,"TK_CycleRadar" },
        { 206,"TK_ConModeMiniTeam" },{ 207,"TK_CommandFinishTM" },{ 208,"TK_RollModifier" },
        { 209,"TK_TrackCommandView" },{ 210,"TK_ConModeTeleport" },{ 211,"TK_ToggleMouse" },
        { 212,"TK_PromoteTurret1" },{ 213,"TK_PromoteTurret2" },{ 214,"TK_PromoteTurret3" },
        { 215,"TK_PromoteTurret4" },{ 216,"TK_VoteYes" },{ 217,"TK_VoteNo" },
        { 218,"TK_ScrnShot" },{ 219,"TK_TargetAlliedBase" },
        { 220,"TK_TargetAlliedBaseNearest" },{ 221,"TK_TargetAlliedBasePrev" },
    };

    /// <summary>Read the corpus <c>defaultinputmap.mdl</c> from the configured
    /// artwork path when present, else the embedded copy. Used at startup and
    /// by the native-fidelity tests.</summary>
    public static string LoadSourceText(string? artworkPath = null)
    {
        if (!string.IsNullOrEmpty(artworkPath))
        {
            var candidate = Path.Combine(artworkPath, "defaultinputmap.mdl");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }
        return EmbeddedText;
    }
}
