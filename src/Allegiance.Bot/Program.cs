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
            "usage: allegiance.bot <port> <playerName> [forwardSeconds] [yawSeconds]\n" +
            "  connects to a running AllegianceCore server, logs on, commands a\n" +
            "  full-forward-thrust-then-yaw manoeuvre, and prints the observed\n" +
            "  trajectory (default forward 4s, yaw 2s).");
        return 2;
    }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 2 || !ushort.TryParse(args[0], out var port))
            return Usage();
        string name = args[1];
        double fwd = args.Length > 2 && double.TryParse(args[2], out var f) ? f : 4.0;
        double yaw = args.Length > 3 && double.TryParse(args[3], out var y) ? y : 2.0;

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
        bot.Connect(port, name);
        Console.WriteLine($"[bot] connected to 127.0.0.1:{port} as '{name}'");

        bot.SendLogonRequest(request);
        var ack = bot.WaitForAck(5000);
        Console.WriteLine($"[bot] LOGONACK: validated={ack.FValidated} shipID={ack.ShipID} (\"{ack.CharNameOrFailureReason}\")");
        if (!ack.FValidated) return 1;

        var set = bot.WaitForShipAssignment(5000);
        Console.WriteLine($"[bot] ship {set.Ship.ShipId} assigned to sector {set.SectorId} at {Fmt(set.Ship.Position)}, " +
                          $"heading {Fmt(set.Ship.Orientation.GetForward())}");

        // ── manoeuvre 1: full forward thrust ───────────────────────────────
        bot.CommandControls(new ControlData(0f, 0f, 0f, 1f));   // throttle +1, no buttons → cruise
        Console.WriteLine($"[bot] commanding full forward thrust for {fwd}s");
        Trajectory fwdTraj = Observe(bot, TimeSpan.FromSeconds(fwd));

        // ── manoeuvre 2: add a yaw while keeping thrust ────────────────────
        bot.CommandControls(new ControlData(1f, 0f, 0f, 1f));   // full yaw + forward
        Console.WriteLine($"[bot] commanding a yaw (still thrusting) for {yaw}s");
        Trajectory yawTraj = Observe(bot, TimeSpan.FromSeconds(yaw));

        Print("phase 1 (full forward)", fwdTraj);
        Print("phase 2 (forward + yaw)", yawTraj);
        Console.WriteLine($"[bot] total observations: {fwdTraj.Samples.Count + yawTraj.Samples.Count}, " +
                          $"final position {Fmt(yawTraj.Samples.Count > 0 ? yawTraj.Last : fwdTraj.Last)}");
        return 0;
    }

    private static Trajectory Observe(Bot bot, TimeSpan duration)
    {
        bot.ResetObservations();
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < duration) Thread.Sleep(10);
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
