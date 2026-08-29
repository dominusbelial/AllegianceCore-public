using Allegiance.Server;

return ServerMain.RunAsync(args);

internal static partial class ServerMain
{
    /// <summary>
    /// Entry point: split args into --config and `--key value` / `--key=value`
    /// overrides, load the config, then hand control to <see cref="Server"/>.
    /// All of the heavy lifting lives in <see cref="Allegiance.Server.Server"/>
    /// so it can be driven from tests without spawning a process.
    /// </summary>
    public static int RunAsync(string[] args)
    {
        string configPath = "server.json";
        var overrides = new List<(string Key, string Value)>();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--config" || a == "--config=")
            {
                if (a == "--config") { if (i + 1 >= args.Length) { Console.Error.WriteLine("--config needs a path"); return 2; } i++; configPath = args[i]; }
            }
            else if (a.StartsWith("--config=", StringComparison.Ordinal))
            {
                configPath = a["--config=".Length..];
            }
            else if (a.StartsWith("--", StringComparison.Ordinal))
            {
                string key, val;
                int eq = a.IndexOf('=');
                if (eq > 0) { key = a[2..eq]; val = a[(eq + 1)..]; }
                else { key = a[2..]; if (i + 1 >= args.Length) { Console.Error.WriteLine($"--{key} needs a value"); return 2; } i++; val = args[i]; }
                overrides.Add((key, val));
            }
            else
            {
                Console.Error.WriteLine($"unexpected positional argument '{a}'");
                return 2;
            }
        }

        try
        {
            return new Server().RunAsync(configPath, overrides);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"server failed: {e.Message}");
            return 1;
        }
    }
}
