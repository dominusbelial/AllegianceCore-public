using System.Diagnostics;
using System.Text.Json;

if (args is ["--stop-proof-worker", var workerMode])
{
    if (workerMode == "paused")
    {
        Console.In.ReadToEnd(); // Driver exit closes this private pipe.
        Thread.Sleep(500);
    }
    else Thread.Sleep(TimeSpan.FromMinutes(1));
    return 0;
}

// Stand in for the bot driver only when started with its private host arguments.
if (args.Contains("--skirmish"))
{
    if (Console.ReadLine() != "start") return 1;
    string mode = File.ReadAllText("mode");
    Console.Error.WriteLine("driver started");
    if (mode == "partial")
    {
        // Use the real supervisor's ownership setup, then exit before sending ready.
        int parent = int.Parse(args[Array.IndexOf(args, "--parent") + 1]);
        Allegiance.Core.ProcessLifetime.WatchOwner(parent, CancellationToken.None);
        using var unannounced = StartWorker("paused");
        File.WriteAllText(Path.Combine("..", "partial-worker"), unannounced.Id.ToString());
        Console.Error.Write(new string('x', 2 * 1024 * 1024));
        return 1;
    }
    using var worker = mode is "slow" or "paused" ? StartWorker(mode) : null;
    int[] workers = worker is null ? [] : [worker.Id];
    Console.WriteLine(JsonSerializer.Serialize(new { type = "ready", pids = workers }));
    if (mode == "bad-log")
    {
        Console.Error.Write(new string('x', 2 * 1024 * 1024));
        Console.Error.Flush();
        File.WriteAllText("drained", "");
    }
    while (Console.ReadLine() is { } line)
    {
        using var command = JsonDocument.Parse(line);
        if (mode == "paused" && !command.RootElement.GetProperty("paused").GetBoolean())
            continue; // Leave the resume request outstanding at Ctrl+F10.
        Console.WriteLine(JsonSerializer.Serialize(new { type = "reply", id = command.RootElement.GetProperty("id").GetInt64(), error = (string?)null }));
    }
    if (mode == "slow") Thread.Sleep(TimeSpan.FromSeconds(30));
    return 0;
}

return await Allegiance.SinglePlayer.SkirmishSmoke.RunAsync(args);

static Process StartWorker(string mode)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, RedirectStandardInput = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
    start.ArgumentList.Add("--stop-proof-worker");
    start.ArgumentList.Add(mode);
    return Process.Start(start)!;
}
