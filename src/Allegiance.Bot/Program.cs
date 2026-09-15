using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Bot;

/// <summary>
/// Console bot (step 4): connect → logon → receive its ship via SET_CLUSTER →
/// command full forward thrust, then a yaw → observe the SINGLE_SHIP_UPDATE
/// position feed over a few seconds → print the trajectory.
/// </summary>
internal static class Program
{
    private static int Usage()
    {
        Console.Error.WriteLine(
            "usage: allegiance.bot <port> <playerName> [forwardSeconds] [yawSeconds] [--host <host>]\n" +
            "  connects to a running AllegianceCore server, logs on, commands a\n" +
            "  full-forward-thrust-then-yaw manoeuvre, and prints the observed\n" +
            "  trajectory (default forward 4s, yaw 2s). --host defaults to 127.0.0.1\n" +
            "  (existing positional-arg call sites are unaffected).");
        return 2;
    }

    private static async Task<int> Main(string[] args)
    {
        // --host <host> is a flag, not a new positional, so every existing
        // <port> <name> [fwd] [yaw] call site tonight keeps working unchanged
        // (2026-09-15: Bot.Connect previously hardcoded 127.0.0.1 regardless
        // of any host argument — see Bot.cs's Connect for the matching fix).
        string host = "127.0.0.1";
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--host" && i + 1 < args.Length) { host = args[++i]; continue; }
            positional.Add(args[i]);
        }

        if (positional.Count < 2 || !ushort.TryParse(positional[0], out var port))
            return Usage();
        string name = positional[1];
        double fwd = positional.Count > 2 && double.TryParse(positional[2], out var f) ? f : 4.0;
        double yaw = positional.Count > 3 && double.TryParse(positional[3], out var y) ? y : 2.0;

        var request = new LogonRequest
        {
            CharacterName = name,
            ZoneTicket = new byte[] { 0x01, 0x02, 0x03, 0x04 },
            CDKey = "AAAABBBBCCCC",
            MissionPassword = "pw",
            FedsrvVer = LogonRequest.MsgVer,
            Time = 707u,
            DwCookie = 0xDEADC0DE,
            CrcFileList = -1,
            SteamAuthTicket = new byte[16],
            SteamAuthTicketLength = 0,
            SteamID = 76561198000000000ul,
            DrmHash = "console-bot",
        };

        using var bot = new Bot();
        bot.Connect(port, name, host);
        Console.WriteLine($"[bot] connected to {host}:{port} as '{name}'");

        bot.SendLogonRequest(request);
        var ack = bot.WaitForAck(5000);
        Console.WriteLine($"[bot] LOGONACK: validated={ack.FValidated} shipID={ack.ShipID} (\"{ack.CharNameOrFailureReason}\")");
        if (!ack.FValidated) return 1;

        var set = bot.WaitForShipAssignment(5000);
        Console.WriteLine($"[bot] ship {set.Ship.ShipId} assigned to sector {set.SectorId} at {Fmt(set.Ship.Position)}, " +
                          $"heading {Fmt(set.Ship.Orientation.GetForward())}");

        // Ready up so a real mission can cross its start countdown (P180/P188
        // auto-start requires every occupied side to be aggregate-ready). A
        // plain console bot that flies should participate in a live mission,
        // not sit unready and keep the mission pinned in NotStarted forever.
        bot.SendPlayerReady(true, set.Ship.ShipId);
        Console.WriteLine($"[bot] sent PLAYER_READY for ship {set.Ship.ShipId}");

        // Ships spawn docked; undock so the bot actually leaves the station and
        // flies, matching the MonoGame client's own auto-undock (Game1.cs).
        bot.SendDocked();
        Console.WriteLine($"[bot] sent undock for ship {set.Ship.ShipId}");

        // ── manoeuvre 1: full forward thrust ───────────────────────────────
        var fwdControls = new ControlData(0f, 0f, 0f, 1f);   // throttle +1, no buttons → cruise
        Console.WriteLine($"[bot] commanding full forward thrust for {fwd}s");
        Trajectory fwdTraj = Observe(bot, fwdControls, TimeSpan.FromSeconds(fwd));

        // ── manoeuvre 2: add a yaw while keeping thrust ────────────────────
        var yawControls = new ControlData(1f, 0f, 0f, 1f);   // full yaw + forward
        Console.WriteLine($"[bot] commanding a yaw (still thrusting) for {yaw}s");
        Trajectory yawTraj = Observe(bot, yawControls, TimeSpan.FromSeconds(yaw));

        Print("phase 1 (full forward)", fwdTraj);
        Print("phase 2 (forward + yaw)", yawTraj);
        Console.WriteLine($"[bot] total observations: {fwdTraj.Samples.Count + yawTraj.Samples.Count}, " +
                          $"final position {Fmt(yawTraj.Samples.Count > 0 ? yawTraj.Last : fwdTraj.Last)}");
        return 0;
    }

    /// <summary>Resend <paramref name="controls"/> every tick for the whole
    /// observation window, not just once. A single fire-and-forget
    /// <c>CommandControls</c> call can race the undock (or the pre-game hold)
    /// and be silently dropped, leaving the ship stationary for the entire
    /// duration with no retry — confirmed live. A real client resends its
    /// current controls continuously (every frame); this now does the same.</summary>
    private static Trajectory Observe(Bot bot, ControlData controls, TimeSpan duration)
    {
        bot.ResetObservations();
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < duration)
        {
            bot.CommandControls(controls);
            Thread.Sleep(50);   // ~20 Hz, matching the server's own tick rate
        }
        return new Trajectory(bot.Observations);
    }

    private static void Print(string label, Trajectory t)
    {
        Console.WriteLine($"--- {label} ---");
        foreach (var s in t.Samples)
            Console.WriteLine($"  tick {s.Tick,4}  pos {Fmt(s.Position),34}  vel {Fmt(s.Velocity),30}  heading {Fmt(s.Orientation.GetForward())}");
        if (t.Samples.Count == 0) Console.WriteLine("  (no SINGLE_SHIP_UPDATE observations)");
    }

    private static string Fmt(Vector3 v) => $"({v.X,8:0.###},{v.Y,8:0.###},{v.Z,8:0.###})";

    private sealed record Trajectory(IReadOnlyList<Bot.ShipObservation> Samples)
    {
        public Vector3 Last => Samples[^1].Position;
    }
}
