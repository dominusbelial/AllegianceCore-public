using System.Reflection;
using System.Text;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame;

// Observe the existing action logs at emission, rather than sampling Messages
// after a frame (which can already have advanced to the next goal).
sealed class BasicFlightTrace : TextWriter
{
    private readonly TextWriter _original = Console.Out;
    private readonly StreamWriter _file;
    private readonly Game1 _game;
    public readonly List<FlightEvent> Events = [];
    public override Encoding Encoding => _original.Encoding;

    public BasicFlightTrace(Game1 game, string directory)
    {
        _game = game;
        _file = new StreamWriter(Path.Combine(directory, "flight-events.jsonl")) { AutoFlush = true };
        Console.SetOut(this);
    }

    public override void WriteLine(string? value)
    {
        _original.WriteLine(value);
        string? kind = value?.StartsWith("[training] prompt: ", StringComparison.Ordinal) == true ? "prompt"
            : value?.StartsWith("[training] voice ", StringComparison.Ordinal) == true ? "voice"
            : value?.StartsWith("[training] goal ", StringComparison.Ordinal) == true ? "goal" : null;
        if (kind is null) return;
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var session = (TrainingSession?)typeof(Game1).GetField("_trainingSession", hidden)!.GetValue(_game);
        double clock = (double)typeof(Game1).GetField("_trainingClock", hidden)!.GetValue(_game)!;
        string text = kind switch
        {
            "prompt" => value!["[training] prompt: ".Length..],
            "voice" => value!["[training] voice ".Length..].Split(' ')[0],
            _ => value!
        };
        float frameDelta = (float)typeof(Game1).GetField("_trainingFrameDelta", hidden)!.GetValue(_game)!;
        var entry = new FlightEvent(Events.Count + 1, clock, frameDelta, session?.Mission.ActiveGoalPath ?? "", kind, text);
        Events.Add(entry);
        _file.WriteLine(JsonSerializer.Serialize(entry));
    }

    public void Validate(string? policy)
    {
        (string Goal, string Text, string Voice)[] prompts =
        [
            ("3.6", "Press the SPACEBAR when you are ready to proceed.", "tm_2_11r"),
            ("4.7", "Press BACKSLASH (\\) or set your joystick throttle to maximum for full thrust.", "tm_2_16r"),
            ("5.3", "Use your scroll wheel or joystick throttle to adjust thrust.", "tm_2_18r"),
            ("6.16", "Press the SPACEBAR when you are ready to proceed.", "tm_2_24r"),
            ("7.13", "Press and hold the SPACE key to fire the booster.", "tm_2_33")
        ];
        var messages = Events.Where(e => e.Kind == "prompt").ToArray();
        bool waiting = policy == "reminders";
        // The keyboard booster (7.13) reminds with a repeat of its own text (P941 keeps
        // native's :747 wrong-key tm_2_33 voice from ever playing); the other four goals
        // repeat their native voice and never reissue text.
        var expectedText = prompts.Select(p => (p.Goal, p.Text))
            .Concat(waiting ? [("7.13", prompts[^1].Text)] : Array.Empty<(string, string)>());
        Require(messages.Select(e => (e.Goal, e.Text)).SequenceEqual(expectedText),
            "exact prompt order/goal/text differs");
        int repeats = 0;
        foreach (var (goal, _, voice) in prompts)
        {
            // mission2.cpp:350-355,436-441,475-481,629-634,741-748:
            // four initial voices; keyboard booster has no initial voice.
            var goalMessages = messages.Where(e => e.Goal == goal).ToArray();
            var prompt = goalMessages[0];
            var sounds = Events.Where(e => e.Kind == "voice" && e.Goal == goal).ToArray();
            bool textReminder = goal == "7.13";
            int count = textReminder ? 0 : 1 + (waiting ? 1 : 0);
            Require(sounds.Select(e => e.Text).SequenceEqual(Enumerable.Repeat(voice, count)),
                $"{goal}: expected {count} occurrences of {voice}, got {string.Join(',', sounds.Select(e => e.Text))}");
            if (!textReminder) Require(sounds[0].Clock == prompt.Clock, $"{goal}: initial voice was not issued with the prompt");
            float periodStart = (float)prompt.Clock;
            foreach (var reminder in textReminder ? goalMessages.Skip(1) : sounds.Skip(1))
            {
                // trainingmission.cpp:927-932; periodiccondition.cpp:52-65:
                // strict >30, on the first evaluated frame past the boundary.
                float now = (float)reminder.Clock;
                float previous = (float)(reminder.Clock - reminder.FrameDelta);
                Require(now - periodStart > 30 && previous - periodStart <= 30,
                    $"{goal}: reminder at {now - periodStart:R}s, previous frame {previous - periodStart:R}s");
                periodStart += 30;
                repeats++;
            }
        }
        Console.WriteLine($"[p1050] REMINDERS_PASS policy={policy ?? "default"} prompts={messages.Length} repeats={repeats}");

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("P1050: " + message);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Console.SetOut(_original); _file.Dispose(); }
        base.Dispose(disposing);
    }
}

sealed record FlightEvent(int Sequence, double Clock, float FrameDelta, string Goal, string Kind, string Text);
