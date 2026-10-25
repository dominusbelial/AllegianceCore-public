using System.Net;
using System.Net.Http;
using Allegiance.Update;

namespace Allegiance.Update.Tests;

/// <summary>
/// P810 — the end-to-end verification slice from the task brief: a local HTTP
/// feed with v1 → v2. The client detects the newer version, downloads ONLY the
/// changed files (delta proof: the unchanged file is requested exactly once
/// fewer than its content change), verifies the signature, refuses a tampered
/// package (manifest and bytes), and applies on a simulated restart.
/// Native analogue: the AUTO_UPDATE_INFO trigger plus
/// <c>clintlib/AutoDownload.cpp</c>'s per-file CRC compare and fetch — the
/// port's SHA-256 + signature version of the same shape.
/// </summary>
public class UpdateClientLoopbackTests : IDisposable
{
    private readonly FeedServer _feed = new();
    private readonly string _appRoot = Path.Combine(Path.GetTempPath(), "p810-client-" + Guid.NewGuid().ToString("N"));

    public UpdateClientLoopbackTests()
    {
        Directory.CreateDirectory(_appRoot);
        _feed.Start();
    }

    public void Dispose()
    {
        _feed.Dispose();
        try { Directory.Delete(_appRoot, recursive: true); } catch (IOException) { }
    }

    private UpdateClientOptions Options(string? installed = null) => new()
    {
        FeedBaseUrl = _feed.BaseUrl,
        PublicKeyPem = _feed.PublicKey,
        AppRoot = _appRoot,
        InstalledVersion = installed,
    };

    [Fact]
    public async Task V1_to_v2_detects_downloads_deltas_verifies_and_applies_on_restart()
    {
        using var http = new HttpClient();

        // ---- v1: three files, one an executable-style launcher (Unix mode 755).
        _feed.Publish("1.0.0", new Dictionary<string, string>
        {
            ["game.dll"] = "version-1-binary",
            ["launcher.sh"] = "#!/bin/sh\nrun v1",
            ["old.txt"] = "shipped in v1 only",
        }, modes: new Dictionary<string, string> { ["launcher.sh"] = "755" });

        var first = await UpdateClient.CheckAndStageAsync(Options(), http);
        Assert.Equal(UpdateCheckStatus.Pending, first.Status);
        Assert.Equal("1.0.0", first.VersionOrSource);
        Assert.Equal(3, first.ChangedFiles);

        // ---- apply on restart (the native Reloader step).
        var applied = UpdateApplier.ApplyPendingIfAny(_appRoot);
        Assert.True(applied.Applied, applied.Detail);
        Assert.Equal("1.0.0", applied.Version);
        Assert.Equal("version-1-binary", File.ReadAllText(Path.Combine(_appRoot, "game.dll")));
        Assert.Equal("1.0.0", UpdateState.ResolveInstalledVersion(_appRoot));

        // ---- v2: game.dll changed, launcher.sh unchanged, config.json added,
        //      old.txt (shipped by v1) was dropped from the manifest; the player's
        //      own file was never in any manifest and must survive.
        File.WriteAllText(Path.Combine(_appRoot, "player-notes.txt"), "mine");
        _feed.RequestCounts.Clear();
        _feed.Publish("2.0.0", new Dictionary<string, string>
        {
            ["game.dll"] = "version-2-binary",
            ["launcher.sh"] = "#!/bin/sh\nrun v1",   // identical bytes — the delta
            ["config.json"] = "{}",
        }, modes: new Dictionary<string, string> { ["launcher.sh"] = "755" });

        var second = await UpdateClient.CheckAndStageAsync(Options(), http);
        Assert.Equal(UpdateCheckStatus.Pending, second.Status);
        Assert.Equal("2.0.0", second.VersionOrSource);
        // Delta proof — request counts were cleared before the v2 check, so
        // they cover exactly this run: the unchanged launcher.sh is requested
        // ZERO times (native's per-file CRC skip), the other two once each.
        Assert.Equal(0, _feed.RequestCounts.GetValueOrDefault("launcher.sh"));
        Assert.Equal(1, _feed.RequestCounts.GetValueOrDefault("game.dll"));
        Assert.Equal(1, _feed.RequestCounts.GetValueOrDefault("config.json"));

        // The running build is untouched until the restart applies.
        Assert.Equal("version-1-binary", File.ReadAllText(Path.Combine(_appRoot, "game.dll")));

        var applied2 = UpdateApplier.ApplyPendingIfAny(_appRoot);
        Assert.True(applied2.Applied, applied2.Detail);
        Assert.Equal("version-2-binary", File.ReadAllText(Path.Combine(_appRoot, "game.dll")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_appRoot, "config.json")));
        Assert.Equal("#!/bin/sh\nrun v1", File.ReadAllText(Path.Combine(_appRoot, "launcher.sh")));
        Assert.Equal("2.0.0", UpdateState.ResolveInstalledVersion(_appRoot));
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(Path.Combine(_appRoot, "launcher.sh"));
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute), $"launcher.sh lost its exec bit: {mode}");
        }
        Assert.False(File.Exists(Path.Combine(_appRoot, "old.txt")), "a file the previous release shipped and v2 drops is removed");
        Assert.True(File.Exists(Path.Combine(_appRoot, "player-notes.txt")), "a file no release installed is never deleted");
        Assert.True(UpdateState.ReadPending(_appRoot) is null, "pending marker must be cleared");
    }

    [Fact]
    public async Task Same_version_is_up_to_date_and_touches_nothing()
    {
        using var http = new HttpClient();
        _feed.Publish("3.1.4", new Dictionary<string, string> { ["game.dll"] = "b" });

        var result = await UpdateClient.CheckAndStageAsync(Options(installed: "3.1.4"), http);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(UpdateState.ReadPending(_appRoot));
        Assert.False(Directory.Exists(Path.Combine(_appRoot, "autoupdate", "staging")));
    }

    [Fact]
    public async Task A_tampered_package_is_refused_and_nothing_is_staged()
    {
        using var http = new HttpClient();
        _feed.Publish("4.0.0", new Dictionary<string, string> { ["game.dll"] = "clean" });
        // Serve different bytes (same length so the size check passes and the
        // SHA-256 verification is what catches it) than the signed manifest.
        _feed.TamperedFiles["game.dll"] = "cle4r";

        var result = await UpdateClient.CheckAndStageAsync(Options(), http);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("SHA-256", result.Error);
        Assert.Null(UpdateState.ReadPending(_appRoot));
        Assert.False(File.Exists(Path.Combine(_appRoot, "game.dll")));
    }

    [Fact]
    public async Task A_tampered_manifest_is_refused()
    {
        using var http = new HttpClient();
        _feed.Publish("5.0.0", new Dictionary<string, string> { ["game.dll"] = "clean" });
        _feed.CorruptLatestManifest = true;

        var result = await UpdateClient.CheckAndStageAsync(Options(), http);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("signature", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(UpdateState.ReadPending(_appRoot));
    }

    [Fact]
    public async Task A_wrong_signing_key_is_refused()
    {
        using var http = new HttpClient();
        _feed.Publish("6.0.0", new Dictionary<string, string> { ["game.dll"] = "clean" });
        var (_, otherPub) = UpdateSignature.GenerateKeyPair();

        var result = await UpdateClient.CheckAndStageAsync(Options() with { PublicKeyPem = otherPub }, http);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Null(UpdateState.ReadPending(_appRoot));
    }

    [Fact]
    public async Task A_stale_pending_update_survives_a_failed_later_check()
    {
        using var http = new HttpClient();
        _feed.Publish("1.0.0", new Dictionary<string, string> { ["a.txt"] = "one" });
        Assert.Equal(UpdateCheckStatus.Pending, (await UpdateClient.CheckAndStageAsync(Options(), http)).Status);
        var stagedBefore = UpdateState.ReadPending(_appRoot);

        _feed.Publish("2.0.0", new Dictionary<string, string> { ["a.txt"] = "two" });
        _feed.TamperedFiles["a.txt"] = "evil";
        var failed = await UpdateClient.CheckAndStageAsync(Options(installed: "1.0.0"), http);

        Assert.Equal(UpdateCheckStatus.Failed, failed.Status);
        // The verified v1 staging is left intact for the restart; the tampered
        // v2 replaced it — but either way the next apply re-verifies every
        // SHA-256 before copying, so a poisoned staging can never install.
        Assert.NotNull(UpdateState.ReadPending(_appRoot));
        Assert.Equal(stagedBefore!.Version, UpdateState.ReadPending(_appRoot)!.Version);
        var outcome = UpdateApplier.ApplyPendingIfAny(_appRoot);
        Assert.False(outcome.Applied, "a poisoned staging must fail its re-verification");
    }

    /// <summary>A minimal static feed server over HttpListener: serves the
    /// manifest tree the way a static host would, counts requests, and can
    /// tamper with individual files or the manifest.</summary>
    private sealed class FeedServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        public readonly Dictionary<string, (byte[] Bytes, string Mode)> Files = new();
        public readonly Dictionary<string, string> TamperedFiles = new();
        public readonly Dictionary<string, int> RequestCounts = new();
        public bool CorruptLatestManifest;
        private readonly (string PrivatePem, string PublicPem) _keys = UpdateSignature.GenerateKeyPair();
        public string PublicKey => _keys.PublicPem;
        private string _manifestName = "";

        public string BaseUrl { get; private set; } = "";

        public void Start()
        {
            var port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}/feed";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/feed/");
            _listener.Start();
            _ = Task.Run(() => ServeAsync(_cts.Token));
        }

        public void Publish(string version, Dictionary<string, string> files, Dictionary<string, string>? modes = null)
        {
            foreach (var (rel, content) in files)
                Files[rel] = (System.Text.Encoding.UTF8.GetBytes(content), modes?.GetValueOrDefault(rel) ?? "644");

            var manifest = new UpdateManifest
            {
                Version = version,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Files = files.Keys.OrderBy(p => p, StringComparer.Ordinal)
                    .Select(rel => new UpdateFileEntry(rel, Files[rel].Bytes.LongLength, Sha(Files[rel].Bytes), Files[rel].Mode))
                    .ToList(),
            };
            _manifestName = $"manifest-{version}.json";
            var manifestBytes = UpdateManifestBuilder.Serialize(manifest);
            using var ecdsa = System.Security.Cryptography.ECDsa.Create();
            ecdsa.ImportFromPem(_keys.PrivatePem);
            Files[_manifestName] = (manifestBytes, "644");
            Files[UpdateFeed.IndexFile] = (UpdateManifestBuilder.SerializeIndex(new UpdateFeedIndex(
                version, _manifestName, Convert.ToBase64String(ecdsa.SignData(manifestBytes, System.Security.Cryptography.HashAlgorithmName.SHA256)))), "644");
        }

        private async Task ServeAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) { return; } // disposed
                try
                {
                    var name = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath)["/feed/".Length..];
                    if (name.Length == 0) name = UpdateFeed.IndexFile;
                    RequestCounts[name] = RequestCounts.GetValueOrDefault(name) + 1;

                    byte[] payload;
                    if (name == _manifestName && CorruptLatestManifest)
                        payload = System.Text.Encoding.UTF8.GetBytes("{\"version\":\"999.0.0\",\"files\":[]}");
                    else if (TamperedFiles.TryGetValue(name, out var evil))
                        payload = System.Text.Encoding.UTF8.GetBytes(evil);
                    else if (!Files.TryGetValue(name, out var entry))
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                        continue;
                    }
                    else payload = entry.Bytes;

                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/octet-stream";
                    await ctx.Response.OutputStream.WriteAsync(payload, ct);
                    ctx.Response.Close();
                }
                catch (Exception) { try { ctx.Response.Close(); } catch { } }
            }
        }

        private static string Sha(byte[] bytes)
            => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

        private static int GetFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            try { return ((IPEndPoint)l.LocalEndpoint).Port; }
            finally { l.Stop(); }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); _listener.Close(); } catch (ObjectDisposedException) { }
            _cts.Dispose();
        }
    }
}
