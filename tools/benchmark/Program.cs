using Allegiance.SinglePlayer;
using Allegiance.SinglePlayer.Benchmark;
using Allegiance.ClientCore.Replay;

if (args.Length > 1 && args[0] == "--inspect-replay")
{
    var replay = new BattleReplay(args[1]);
    using var session = new Allegiance.ClientCore.Session.ConnectionSession();
    var maxPoses = new int[3]; var maxVisible = new int[3]; var maxEnemies = new int[3]; int projectiles = 0;
    while (!replay.Finished)
    {
        replay.Advance(session);
        int phase = replay.CameraPhase;
        var enemies = session.World.Ships.Values.Where(s => s.SideId == 1 && s.Latest is not null).ToArray();
        maxEnemies[phase] = Math.Max(maxEnemies[phase], enemies.Length);
        maxPoses[phase] = Math.Max(maxPoses[phase], enemies.Count(s => s.PoseSectorId == replay.ViewedSector));
        maxVisible[phase] = Math.Max(maxVisible[phase], enemies.Count(s => replay.ViewedSector is { } sector && s.CanDrawInSector(sector)));
        projectiles = Math.Max(projectiles, session.World.Projectiles.Count);
    }
    Console.WriteLine($"Enemy poses by phase: {string.Join(',', maxEnemies)}; in viewed sector: {string.Join(',', maxPoses)}; drawable: {string.Join(',', maxVisible)}; projectiles={projectiles}");
    return 0;
}

if (args.Length > 0 && args[0] == "--record-once")
{
    double seconds = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 150;
    if (!double.IsFinite(seconds) || seconds < 1 || seconds > 180) throw new ArgumentException("Duration must be 1..180 seconds");
    using var host = await SkirmishHost.StartAsync(new() { Seed = 729, ResearchSpeed = 8 });
    using var recording = new WireRecording(args[1]);
    var result = await SkirmishSmoke.PlayAsync(host, TimeSpan.FromSeconds(seconds), recording: recording, benchmarkCamera: true);
    Console.WriteLine($"Recorded real loopback battle: shots={result.Shots}, hits={result.Hits}, bots={result.Bots}, running={result.Running}");
    return result.Running && result.BotsAlive && result.Shots > 0 ? 0 : 1;
}
var command = BenchmarkCommand.Parse(args) ?? throw new ArgumentException("Use --benchmark <mode> --benchmark-headless");
return await BenchmarkCommand.RunHeadlessAsync(command);
