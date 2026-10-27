using System.Diagnostics;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P832aMissionRulesInventoryTests
{
    [SkippableFact]
    public void Every_native_mission_body_handler_and_local_rule_helper_has_a_matrix_row()
    {
        Require.NativeSource();
        var native = Allegiance.Core.ExternalPaths.Resolve(Allegiance.Core.ExternalResourceKind.NativeSource);
        if (!Directory.Exists(Path.Combine(native, "FedSrv"))) native = Path.Combine(native, "src");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tools", "p832a-mission-rules-audit.py"))) root = root.Parent;
        Assert.NotNull(root);
        var start = new ProcessStartInfo("python3") { WorkingDirectory = root!.FullName,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "tools/p832a-mission-rules-audit.py", "--native-root", native, "--check" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30000));
        Assert.True(process.ExitCode == 0, output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult());
        Assert.Contains("bodies/cases", output.GetAwaiter().GetResult());
    }
}
