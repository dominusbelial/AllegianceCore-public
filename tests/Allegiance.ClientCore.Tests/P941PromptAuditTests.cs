using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Allegiance.ClientCore.Training;
using Xunit;
using TrainingAction = Allegiance.ClientCore.Training.Action;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P941 — the training prompt/key audit. Walks the port's mission scripts (the
/// goal graph each <see cref="TrainingMission"/> builds) and fails when an
/// on-screen prompt, the voice line it plays, or its too-long reminder does not
/// agree with the action/key the native-default profile binds.
///
/// <para>Native reference: the prompts are transcribed from
/// <c>training/mission2/3/4/5/6/8/10.cpp</c> (read as ISO-8859-1), the keys from
/// <c>Artwork/defaultinputmap.mdl</c> (keyCommandMap lines 3-107,
/// mouseButtonCommandMap lines 109-116). The native bug this task fixes: where
/// FreeAllegiance muted a voice-over that named the old key, the same muted sound
/// id was still passed to <c>CreateTooLongCondition</c> (mission2.cpp:747,
/// mission3.cpp:453/:645/:944), so a hesitating player heard the wrong key 30 s
/// later.</para>
/// </summary>
public sealed class P941PromptAuditTests
{
    // ── graph walking ──────────────────────────────────────────────────────

    /// <summary>Collect every <see cref="Condition"/>/<see cref="Action"/> reachable
    /// from a mission's built goal graph. Reflection is deliberate: the graph is
    /// built by the mission scripts themselves, so this audits the REAL scripts
    /// (not a copy of their text).</summary>
    private static List<object> Collect(TrainingMission mission)
    {
        var all = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var root = typeof(TrainingMission)
            .GetField("_condition", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mission);
        Walk(root, seen, all);
        return all;
    }

    private static void Walk(object? node, HashSet<object> seen, List<object> all)
    {
        if (node is null || !seen.Add(node) || node is not (Condition or TrainingAction)) return;
        all.Add(node);
        for (var t = node.GetType(); t is not null && t != typeof(object); t = t.BaseType)
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public
                                          | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                object? value;
                try { value = f.GetValue(node); } catch (Exception) { continue; }
                if (value is Condition or TrainingAction) Walk(value, seen, all);
                else if (value is IEnumerable list and not string)
                    foreach (var item in list)
                        if (item is Condition or TrainingAction) Walk(item, seen, all);
            }
    }

    private static IEnumerable<string> PromptTexts(IEnumerable<object> graph) =>
        graph.OfType<MessageAction>()
             .SelectMany(m => Messages(m))
             .Select(m => m.Text);

    private static List<(string Text, int SoundId)> Messages(MessageAction m) =>
        (List<(string, int)>)typeof(MessageAction)
            .GetField("_messages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;

    private static int SoundOf(PlaySoundAction p) =>
        (int)typeof(PlaySoundAction).GetField("_soundId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(p)!;

    /// <summary>Every too-long reminder in the graph: the periodic
    /// <see cref="ConditionalAction"/> wrapper plus the action it repeats.</summary>
    private static IEnumerable<(ConditionalAction Reminder, object Inner)> Reminders(IEnumerable<object> graph) =>
        graph.OfType<ConditionalAction>().Select(c => (c, (object)c.InnerAction));

    /// <summary>Mission 2/3 have a joystick branch (<c>HasJoystick</c>,
    /// mission2.cpp:403, mission3.cpp:459-461/:589-591) — the port ships the
    /// keyboard branch, but the audit covers both variants' prompts.</summary>
    private sealed class JoystickMission2(FakeTrainingSite site) : Mission2(site)
    {
        protected override bool HasJoystick => true;
    }

    private sealed class JoystickMission3(FakeTrainingSite site) : Mission3(site)
    {
        protected override bool HasJoystick => true;
    }

    private static readonly (int Mission, bool Joystick, Func<FakeTrainingSite, TrainingMission> Factory)[] Missions =
    [
        (2, false, s => new Mission2(s)), (2, true, s => new JoystickMission2(s)),
        (3, false, s => new Mission3(s)), (3, true, s => new JoystickMission3(s)),
        (4, false, s => new Mission4(s)), (5, false, s => new Mission5(s)),
        (6, false, s => new Mission6(s)), (8, false, s => new Mission8(s)),
        (10, false, s => new Mission10(s)),
    ];

    /// <summary>Every mission variant's built graph: (mission id, joystick branch).</summary>
    private static List<(int Mission, bool Joystick, List<object> Graph)> BuildAll()
    {
        TrainingClock.ResetForTests();
        var graphs = new List<(int, bool, List<object>)>();
        foreach (var (id, joystick, factory) in Missions)
        {
            var site = new FakeTrainingSite();
            var mission = factory(site);
            mission.Start();
            graphs.Add((id, joystick, Collect(mission)));
        }
        TrainingClock.ResetForTests();
        return graphs;
    }

    // ── the audit ──────────────────────────────────────────────────────────

    [Fact]
    public void Every_audited_prompt_is_present_in_the_mission_script()
    {
        var graphs = BuildAll();
        foreach (var prompt in TrainingPromptAudit.All)
        {
            var texts = graphs.Where(g => g.Mission == prompt.MissionId)
                              .SelectMany(g => PromptTexts(g.Graph)).ToList();
            Assert.True(texts.Any(t => t == prompt.Text),
                $"mission {prompt.MissionId}: prompt text not found in the script: \"{prompt.Text}\" "
                + $"(native {prompt.NativeLine}). Found: {string.Join(" | ", texts.Distinct())}");
        }
    }

    /// <summary>Case-insensitive token match; a purely alphabetic token must be a
    /// whole word ("TAB" must not match "acceptable").</summary>
    private static bool Names(string token, string text) =>
        token.All(char.IsAsciiLetter)
            ? System.Text.RegularExpressions.Regex.IsMatch(text, "\\b" + token + "\\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            : text.Contains(token, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Every_prompt_that_names_an_input_is_in_the_audit_table()
    {
        // Coverage guard: a new key-naming prompt in any mission must be added to
        // TrainingPromptAudit (and therefore to the report), not silently ship.
        string[] inputWords =
        [
            "SPACEBAR", "SPACE key", "BACKSLASH", "scroll wheel", "ENTER", "TAB", "TILDE",
            "INSERT", "Ctrl+1", "F key", "T key", "B key", "G key", "F1", "F4",
            "Left click", "left click", "Right click", "right click", "middle mouse button",
            "right mouse button", "button #3", "#2 button", "trigger button", "Press 5",
        ];
        var graphs = BuildAll();
        foreach (var (missionId, joystick, graph) in graphs)
            foreach (var text in PromptTexts(graph).Distinct())
            {
                bool namesInput = inputWords.Any(w => Names(w, text));
                if (!namesInput) continue;
                Assert.True(TrainingPromptAudit.All.Any(p => p.MissionId == missionId && p.Text == text),
                    $"mission {missionId} (joystick={joystick}): prompt names an input but is not audited: \"{text}\"");
            }
    }

    [Fact]
    public void No_too_long_reminder_replays_a_muted_voice_line()
    {
        // THE reminder bug: native muted the wrong-key recordings but kept passing
        // them to CreateTooLongCondition (mission2.cpp:747, mission3.cpp:453,
        // :645, :944). A reminder must never play one of them — it repeats the
        // on-screen message instead (P941).
        var graphs = BuildAll();
        foreach (var (missionId, joystick, graph) in graphs)
            foreach (var (_, inner) in Reminders(graph))
            {
                if (inner is not PlaySoundAction play) continue;
                var stem = TrainingSoundCatalog.Stem(missionId, SoundOf(play));
                Assert.False(stem is not null
                             && TrainingPromptAudit.MutedReminderStems.Contains(stem),
                    $"mission {missionId} (joystick={joystick}): a too-long reminder still plays the muted recording {stem}");
            }
    }

    [Fact]
    public void Fixed_prompts_repeat_their_on_screen_text_when_the_player_dawdles()
    {
        var graphs = BuildAll();
        foreach (var prompt in TrainingPromptAudit.All.Where(p => p.ReminderFixed))
        {
            var repeated = graphs.Where(g => g.Mission == prompt.MissionId)
                .SelectMany(g => Reminders(g.Graph))
                .Where(r => r.Inner is MessageAction)
                .SelectMany(r => Messages((MessageAction)r.Inner))
                .Select(m => m.Text)
                .ToList();
            Assert.Contains(prompt.Text, repeated);
        }
    }

    [Fact]
    public void Prompt_table_has_one_row_per_named_input_and_cites_native()
    {
        Assert.NotEmpty(TrainingPromptAudit.All);
        Assert.All(TrainingPromptAudit.All, p =>
        {
            Assert.Matches(@"mission\d+\.cpp:\d+", p.NativeLine);
            Assert.False(string.IsNullOrWhiteSpace(p.Text));
            Assert.False(string.IsNullOrWhiteSpace(p.Goal));
            if (p.Input is PromptInput.Keyboard or PromptInput.MouseButton or PromptInput.Wheel)
                Assert.False(string.IsNullOrWhiteSpace(p.Action));
        });
        // The four muted-voice prompts are the ones the reminder fix covers.
        Assert.Equal(4, TrainingPromptAudit.All.Count(p => p.ReminderFixed));
        Assert.Equal(4, TrainingPromptAudit.MutedVoiceStems.Length);
    }
}
