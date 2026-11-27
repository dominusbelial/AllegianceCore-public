using Allegiance.SinglePlayer;

namespace Allegiance.ClientCore.Tests;

public sealed class P1093SkirmishRunFilesTests
{
    [Theory]
    [InlineData("open")]
    [InlineData("write")]
    [InlineData("flush")]
    public async Task Failed_destination_still_consumes_unterminated_stderr_through_EOF(string fault)
    {
        using var input = new MemoryStream(new byte[SkirmishRunFiles.LogBytes * 2]);
        var destination = new FailingLog(fault);
        await SkirmishRunFiles.DrainLogAsync(input, () => fault == "open"
            ? throw new UnauthorizedAccessException("read only") : destination);
        Assert.Equal(input.Length, input.Position);
        if (fault != "open") Assert.True(destination.Closed);
    }

    private sealed class FailingLog(string fault) : MemoryStream
    {
        public bool Closed { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
            => fault == "write" ? ValueTask.FromException(new IOException("disk full")) : base.WriteAsync(buffer, token);
        public override Task FlushAsync(CancellationToken token)
            => fault == "flush" ? Task.FromException(new IOException("disk full")) : base.FlushAsync(token);
        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
            throw new IOException("final flush failed");
        }
    }

    [Fact]
    public void Pruning_keeps_newest_closed_failures_and_leaves_active_and_unrelated_directories_alone()
    {
        var root = Directory.CreateTempSubdirectory("p1093-prune-");
        string Run() => root.CreateSubdirectory(SkirmishRunFiles.Prefix + Guid.NewGuid().ToString("N")).FullName;
        try
        {
            string active = Run();
            string other = root.CreateSubdirectory(SkirmishRunFiles.Prefix + "other").FullName;
            File.WriteAllText(Path.Combine(other, ".failed"), "");
            var runs = new List<string>();
            for (int i = 0; i < SkirmishRunFiles.FailedRuns + 2; i++)
            {
                string dir = Run();
                runs.Add(dir);
                File.WriteAllText(Path.Combine(dir, "server.json"), "config");
                File.WriteAllText(Path.Combine(dir, "err.worker-1.log"), "worker");
                SkirmishRunFiles.Finish(dir, "startup failed before driver launch");
                File.SetLastWriteTimeUtc(Path.Combine(dir, ".failed"), DateTime.UtcNow.AddDays(-20 + i));
            }
            Assert.True(Directory.Exists(active));
            Assert.True(File.Exists(Path.Combine(other, ".failed")));
            Assert.All(runs.Take(2), dir => Assert.False(Directory.Exists(dir)));
            foreach (var dir in runs.Skip(2))
            {
                Assert.Equal(2, Directory.GetFiles(dir).Length);
                Assert.Equal("startup failed before driver launch", File.ReadAllText(Path.Combine(dir, "driver.log")));
            }
        }
        finally { root.Delete(true); }
    }
}
