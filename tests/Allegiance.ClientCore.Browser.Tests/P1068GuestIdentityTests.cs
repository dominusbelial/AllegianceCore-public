using System.Diagnostics;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1068GuestIdentityTests
{
    [Fact]
    public async Task Browser_script_uses_crypto_and_handles_present_absent_and_blocked_storage()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AllegianceCore.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var start = new ProcessStartInfo("node") { WorkingDirectory = root.FullName, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("tools/p1068-guest-identity-tests.cjs");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        Assert.True(process.ExitCode == 0, await error);
        Assert.Contains("P1068_GUEST_IDENTITY_PASS", await output);
    }
}
