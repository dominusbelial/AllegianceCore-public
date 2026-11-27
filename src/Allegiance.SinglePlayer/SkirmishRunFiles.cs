namespace Allegiance.SinglePlayer;

/// <summary>Storage owned by one local skirmish. Only closed failures participate in pruning.</summary>
internal static class SkirmishRunFiles
{
    internal const int LogBytes = 1024 * 1024;
    internal const int FailedRuns = 4;
    internal const string Prefix = "allegiance-skirmish-";
    private const string Closed = ".failed";

    internal static async Task DrainLogAsync(Stream source, Func<Stream> openLog)
    {
        Stream? log = null;
        try
        {
            try { log = openLog(); }
            catch (Exception ex) when (StorageError(ex)) { }
            byte[] buffer = new byte[8192];
            int remaining = LogBytes;
            int read;
            while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                if (log is null || remaining == 0) continue;
                int count = Math.Min(remaining, read);
                try
                {
                    await log.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                    await log.FlushAsync().ConfigureAwait(false);
                    remaining -= count;
                }
                catch (Exception ex) when (StorageError(ex))
                {
                    CloseLog(log);
                    log = null;
                }
            }
        }
        finally { CloseLog(log); }
    }

    private static void CloseLog(Stream? log)
    {
        try { log?.Dispose(); }
        catch (Exception ex) when (StorageError(ex)) { }
    }

    private static bool StorageError(Exception ex) => ex is IOException or UnauthorizedAccessException;

    internal static void Finish(string directory, string? failure)
    {
        try
        {
            if (failure is null)
            {
                Directory.Delete(directory, recursive: true);
                return;
            }

            // The supervisor can also have written worker logs into our working directory.
            string log = Path.Combine(directory, "driver.log");
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo subdirectory) subdirectory.Delete(recursive: true);
                else if (entry.FullName != log) entry.Delete();
            }

            // Startup can fail before the driver has opened its pipes.
            if (!File.Exists(log) || new FileInfo(log).Length == 0)
                File.WriteAllText(log, failure[..Math.Min(failure.Length, LogBytes / 4)]);

            File.WriteAllBytes(Path.Combine(directory, Closed), []);
            var closed = new DirectoryInfo(Path.GetDirectoryName(directory)!)
                .EnumerateDirectories(Prefix + "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(d => Guid.TryParseExact(d.Name[Prefix.Length..], "N", out _))
                .Select(d => new FileInfo(Path.Combine(d.FullName, Closed)))
                .Where(marker => marker.Exists)
                .OrderByDescending(marker => marker.LastWriteTimeUtc)
                .ThenBy(marker => marker.FullName, StringComparer.Ordinal)
                .Skip(FailedRuns).ToArray();
            foreach (var marker in closed)
            {
                try
                {
                    File.Delete(Path.Combine(marker.DirectoryName!, "driver.log"));
                    marker.Delete();
                    marker.Directory!.Delete();
                }
                catch (Exception ex) when (StorageError(ex))
                { Console.Error.WriteLine("[skirmish] pruning failed run: " + ex.Message); }
            }
        }
        catch (Exception ex) when (StorageError(ex))
        { Console.Error.WriteLine("[skirmish] cleaning run files: " + ex.Message); }
    }
}
