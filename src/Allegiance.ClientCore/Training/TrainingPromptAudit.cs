namespace Allegiance.ClientCore.Training;

/// <summary>How a training prompt names its input.</summary>
public enum PromptInput
{
    /// <summary>A keyboard chord (checked against the layout's keyboard map).</summary>
    Keyboard,
    /// <summary>A native mouse button (mouseButtonCommandMap).</summary>
    MouseButton,
    /// <summary>The cruise-throttle wheel (mouseButtonCommandMap 8/9).</summary>
    Wheel,
    /// <summary>A free pointer click in the command camera / sector map — no
    /// action-map chord involved (native console.cpp:1912-1990).</summary>
    Pointer,
    /// <summary>A joystick button (native buttonCommandMap); the port ships the
    /// keyboard branch, so there is no binding to check.</summary>
    Joystick,
}

/// <summary>
/// P941 — one training prompt that names an input: the on-screen
/// <c>MessageAction</c> text, the voice line native plays, the too-long reminder,
/// and the action + key the prompt is asking for.
/// <para><see cref="Named"/> is the canonical label the action map shows for the
/// bound chord (a <c>KeyBinding.Label</c> / <c>MouseButtonBinding.Label</c> style
/// label), so the audit can prove the player's native-default profile really has
/// that key on that action. <see cref="Tokens"/> are the words the on-screen text
/// uses to name it, so the audit also proves the text and the binding agree.</para>
/// </summary>
public sealed record TrainingPrompt(
    int MissionId,
    string Goal,
    string NativeLine,
    string Text,
    string Action,
    PromptInput Input,
    string Named,
    string[] Tokens,
    string? VoiceStem,
    bool VoiceMuted,
    string Reminder,
    bool ReminderFixed = false);

/// <summary>
/// P941 — the audit table behind the report and the
/// <c>P941PromptAuditTests</c> test: every training prompt that names an input,
/// transcribed from the native scripts (<c>training/mission*.cpp</c>) and the
/// port's <c>Mission*.cs</c>.
///
/// <para><b>Muted voice lines.</b> FreeAllegiance rewrote the on-screen prompts
/// for its keymap and muted the 1999 recordings that name the old keys: <c>tm_3_19</c>
/// ("spacebar", mission3.cpp:643), <c>tm_3_38</c> ("control plus space bar", :942),
/// <c>tm_2_33</c> ("tab key", mission2.cpp:746) and <c>tm_3_11r</c> ("C key", :451).
/// <b>Native bug:</b> the same muted sound ids are still passed to
/// <c>CreateTooLongCondition(30.0f, &lt;sound&gt;)</c> (:645, :944, mission2.cpp:747,
/// mission3.cpp:453), so a hesitating player heard the wrong key 30 s later;
/// <see cref="MutedReminderStems"/> is exactly the set that must never play as a
/// reminder (P941 repeats the on-screen text instead, see
/// <c>TrainingMission.CreateTooLongMessageCondition</c>).</para>
/// </summary>
public static class TrainingPromptAudit
{
    /// <summary>Mission ids as used by <see cref="TrainingMission.GetMissionID"/>.</summary>
    public const int Mission2Id = 2, Mission3Id = 3, Mission4Id = 4, Mission5Id = 5,
        Mission6Id = 6, Mission8Id = 8, Mission10Id = 10;

    /// <summary>The wave stems native muted because they name a key the keymap no
    /// longer uses.</summary>
    public static readonly string[] MutedVoiceStems = ["tm_2_33", "tm_3_11r", "tm_3_19", "tm_3_38"];

    /// <summary>The wave stems native still passes to <c>CreateTooLongCondition</c>
    /// for those muted prompts — the reminder bug.</summary>
    public static readonly string[] MutedReminderStems = ["tm_2_33", "tm_3_11", "tm_3_19", "tm_3_38"];

    private static TrainingPrompt Key(int mission, string goal, string nativeLine, string text,
        string action, string named, string[] tokens, string? voice, bool muted = false, string reminder = "",
        bool fixedReminder = false)
        => new(mission, goal, nativeLine, text, action, PromptInput.Keyboard, named, tokens, voice, muted, reminder, fixedReminder);

    private static TrainingPrompt Mouse(int mission, string goal, string nativeLine, string text,
        string action, PromptInput kind, string named, string[] tokens, string? voice = null,
        bool muted = false, string reminder = "", bool fixedReminder = false)
        => new(mission, goal, nativeLine, text, action, kind, named, tokens, voice, muted, reminder, fixedReminder);

    /// <summary>Every prompt that names an input key/button, in mission order.</summary>
    public static readonly IReadOnlyList<TrainingPrompt> All =
    [
        // ── Mission 2, Basic Flight ───────────────────────────────────────────
        Key(Mission2Id, "2.3", "mission2.cpp:352-354",
            "Press the SPACEBAR when you are ready to proceed.",
            "FireBooster", "Space", ["SPACEBAR"], "tm_2_11r"),
        Key(Mission2Id, "2.4", "mission2.cpp:438-440",
            "Press BACKSLASH (\\) or set your joystick throttle to maximum for full thrust.",
            "ThrottleFull", "\\", ["BACKSLASH"], "tm_2_16r"),
        Mouse(Mission2Id, "2.5", "mission2.cpp:478-480",
            "Use your scroll wheel or joystick throttle to adjust thrust.",
            "ThrottleUp", PromptInput.Wheel, "Wheel Up", ["scroll wheel"], "tm_2_18r"),
        Key(Mission2Id, "2.6", "mission2.cpp:631-633",
            "Press the SPACEBAR when you are ready to proceed.",
            "FireBooster", "Space", ["SPACEBAR"], "tm_2_24r"),
        Mouse(Mission2Id, "2.7", "mission2.cpp:737-739",
            "Press and hold button #3 on your joystick to fire the booster.",
            "", PromptInput.Joystick, "", ["button #3"], "tm_2_32"),
        Key(Mission2Id, "2.7", "mission2.cpp:746-748",
            "Press and hold the SPACE key to fire the booster.",
            "FireBooster", "Space", ["SPACE key"], "tm_2_33", muted: true, fixedReminder: true,
            reminder: "native muted tm_2_33 (:746) but reminded with it (:747) - P941 repeats the text"),

        // ── Mission 3, Weapons and Targeting ─────────────────────────────────
        Mouse(Mission3Id, "3.3", "mission3.cpp:450-453",
            "Press the right mouse button to target the objects in the center of your display.",
            "TargetCenter", PromptInput.MouseButton, "Mouse Right", ["right mouse button"], "tm_3_11r",
            muted: true, fixedReminder: true,
            reminder: "native removed tm_3_11r (:451) but reminded with tm_3_11 (:453) - P941 repeats the text"),
        Key(Mission3Id, "3.3", "mission3.cpp:481-484",
            "Press the SPACEBAR when you are ready to proceed.",
            "FireBooster", "Space", ["SPACEBAR"], "tm_3_12r"),
        Key(Mission3Id, "3.4", "mission3.cpp:534-536",
            "Press the SPACEBAR when you are ready to proceed.",
            "FireBooster", "Space", ["SPACEBAR"], "tm_3_15"),
        Mouse(Mission3Id, "3.5", "mission3.cpp:634-637",
            "Fire the main weapon by pressing the trigger button on your joystick, or left click if using a mouse",
            "FireWeapon", PromptInput.MouseButton, "Mouse Left", ["left click"], "tm_3_18"),
        Mouse(Mission3Id, "3.5", "mission3.cpp:643-646",
            "Left click to fire your main weapon.",
            "FireWeapon", PromptInput.MouseButton, "Mouse Left", ["Left click"], "tm_3_19",
            muted: true, fixedReminder: true,
            reminder: "native muted tm_3_19 (:643) but reminded with it (:645) - P941 repeats the text"),
        Key(Mission3Id, "3.7", "mission3.cpp:773-776",
            "Target an enemy using the F key. Target a friendly using the T key.",
            "TargetEnemy", "F", ["F key"], "tm_3_27", muted: true,
            reminder: "native muted tm_3_27 and its reminder (:775-776) - no reminder at all"),
        Key(Mission3Id, "3.7", "mission3.cpp:773-776",
            "Target an enemy using the F key. Target a friendly using the T key.",
            "TargetFriendly", "T", ["T key"], "tm_3_27", muted: true,
            reminder: "native muted tm_3_27 and its reminder (:775-776) - no reminder at all"),
        Mouse(Mission3Id, "3.8", "mission3.cpp:843-845",
            "Fire the main weapon by pressing the trigger button on your joystick.",
            "", PromptInput.Joystick, "", ["trigger button"], "tm_3_31r"),
        Mouse(Mission3Id, "3.8", "mission3.cpp:843-845",
            "Left click to fire your main weapon.",
            "FireWeapon", PromptInput.MouseButton, "Mouse Left", ["Left click"], "tm_3_31r"),
        Mouse(Mission3Id, "3.9", "mission3.cpp:932-935",
            "Fire missiles by pressing the #2 button on your joystick.",
            "", PromptInput.Joystick, "", ["#2 button"], "tm_3_37"),
        Mouse(Mission3Id, "3.9", "mission3.cpp:941-945",
            "Press the middle mouse button to fire your secondary weapon.",
            "FireMissile", PromptInput.MouseButton, "Mouse Middle", ["middle mouse button"], "tm_3_38",
            muted: true, fixedReminder: true,
            reminder: "native muted tm_3_38 (:942) but reminded with it (:945) - P941 repeats the text"),
        Key(Mission3Id, "3.10", "mission3.cpp:1026-1030",
            "Press the SPACE key when you are comfortable with the Inventory Display.",
            "FireBooster", "Space", ["SPACE key"], "tm_3_15"),
        Key(Mission3Id, "3.11", "mission3.cpp:1090-1094",
            "Press the SPACE key when you are ready to go home.",
            "FireBooster", "Space", ["SPACE key"], "tm_3_46"),
        Key(Mission3Id, "3.12", "mission3.cpp:1120-1124",
            "Press the B key to target the outpost.",
            "TargetFriendlyBase", "B", ["B key"], "tm_3_48"),

        // ── Mission 4, Communication and Commands ────────────────────────────
        Key(Mission4Id, "4.2", "mission4.cpp:240-244",
            "Press the ENTER key to begin a chat.",
            "StartChat", "Enter", ["ENTER"], "tm_4_03r"),
        Key(Mission4Id, "4.2", "mission4.cpp:241",
            "Press the TAB key to select 'All'.",
            "CycleChatTarget", "Tab", ["TAB"], "tm_4_03r"),
        Key(Mission4Id, "4.2", "mission4.cpp:242",
            "Press the ENTER key again to send the chat.",
            "StartChat", "Enter", ["ENTER"], "tm_4_03r"),
        Key(Mission4Id, "4.2", "mission4.cpp:265-269",
            "Press the SPACEBAR when you are ready to proceed.",
            "FireBooster", "Space", ["SPACEBAR"], "tm_4_04r"),
        Key(Mission4Id, "4.3", "mission4.cpp:290-302",
            "Press the TILDE (~) key to activate the quickcomm menu.",
            "QuickChat", "`", ["TILDE"], "tm_4_05"),
        Key(Mission4Id, "4.3", "mission4.cpp:301-302",
            "Press the SPACEBAR when you are ready to proceed.",
            "FireBooster", "Space", ["SPACEBAR"], "tm_4_06r"),
        Key(Mission4Id, "4.4", "mission4.cpp:347-351",
            "Press the INSERT or '[' key to accept the command.",
            "AcceptCommand", "[", ["INSERT", "'['"], "tm_4_08r"),
        Key(Mission4Id, "4.5", "mission4.cpp:448-452",
            "Press the INSERT or '[' key to accept the command.",
            "AcceptCommand", "[", ["INSERT", "'['"], "tm_4_15"),
        Key(Mission4Id, "4.6", "mission4.cpp:720-725",
            "Press the G key to activate your cloak.",
            "ToggleCloak", "G", ["G key"], "tm_4_26r"),
        Key(Mission4Id, "4.7", "mission4.cpp:796-801",
            "Target an enemy miner with the F key, and destroy it if you can.",
            "TargetEnemy", "F", ["F key"], "tm_4_29"),
        Key(Mission4Id, "4.8", "mission4.cpp:900-906",
            "Press the INSERT key to accept the command.",
            "AcceptCommand", "[", ["INSERT"], "tm_4_32"),
        Key(Mission4Id, "4.9", "mission4.cpp:930-934",
            "Press the INSERT key to accept the command.",
            "AcceptCommand", "[", ["INSERT"], "tm_4_33"),
        Key(Mission4Id, "4.10", "mission4.cpp:989-991",
            "Press the INSERT key to accept the command.",
            "AcceptCommand", "[", ["INSERT"], null),

        // ── Mission 5, Commanding ────────────────────────────────────────────
        Key(Mission5Id, "5.1", "mission5.cpp:273-277",
            "Press the SPACEBAR when you are ready to proceed.",
            "FireBooster", "Space", ["SPACEBAR"], "tm_5_02"),
        Mouse(Mission5Id, "5.2", "mission5.cpp:295-299",
            "Left click on a Tower Layer to select it.",
            "", PromptInput.Pointer, "", ["Left click"], "tm_5_03r"),
        Mouse(Mission5Id, "5.2", "mission5.cpp:310-313",
            "Left click on 'Build' command.",
            "", PromptInput.Pointer, "", ["Left click"], null),
        Mouse(Mission5Id, "5.2", "mission5.cpp:324-328",
            "Right click in space to deploy tower.",
            "", PromptInput.Pointer, "", ["Right click"], "tm_5_04r"),
        Mouse(Mission5Id, "5.3", "mission5.cpp:372-377",
            "Left click on 'Mars' in the Sector Map to change view.",
            "", PromptInput.Pointer, "", ["Left click"], "tm_5_06r"),
        Mouse(Mission5Id, "5.4", "mission5.cpp:391-394",
            "Left click on a Miner to select it.",
            "", PromptInput.Pointer, "", ["Left click"], "tm_5_07r"),
        Mouse(Mission5Id, "5.4", "mission5.cpp:414-419",
            "Left click on 'Mine' command, and right click on the He3 asteroid to begin mining.",
            "", PromptInput.Pointer, "", ["Left click", "right click"], "tm_5_08r"),

        // ── Missions 6 / 10, hint line ───────────────────────────────────────
        Key(Mission6Id, "6.1", "mission6.cpp:407",
            "Hint: Press F1 to see an overview of your ship's controls.",
            "Help", "F1", ["F1"], null),
        Key(Mission10Id, "10.1", "mission10.cpp:401",
            "Hint: Press F1 to see an overview of your ship's controls.",
            "Help", "F1", ["F1"], null),

        // ── Mission 8, Nanite Repair ────────────────────────────────────────
        Key(Mission8Id, "8.2", "mission8.cpp:182",
            "F4 toggles the inventory. You will want it to be open.",
            "ConModeInventory", "F4", ["F4"], null),
        Key(Mission8Id, "8.3", "mission8.cpp:194-195",
            "Swap the Nanite Repair System into slot 1 by pressing Ctrl+1.",
            "SwapWeapon1", "Ctrl + 1", ["Ctrl+1"], "tm_8_03"),
    ];
}
