using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Allegiance.SinglePlayer.Benchmark;

namespace Allegiance.ClientCore.Tests;

public sealed class BS1BenchmarkSharingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bs1-test-" + Guid.NewGuid());
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-19T00:00:00Z");
    private sealed class Transport : HttpMessageHandler
    {
        public readonly List<(string Method, string Url, string? Json)> Calls = [];
        public bool Offline;
        public HttpStatusCode Status = HttpStatusCode.Created;
        public TaskCompletionSource? Entered, Release;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls.Add((request.Method.Method, request.RequestUri!.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            Entered?.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(token);
            if (Offline) throw new HttpRequestException("Network unavailable");
            return new(Status);
        }
    }
    private BenchmarkSharing Create(Transport transport, string url = "http://localhost:8888") => new(_dir, url, new HttpClient(transport), () => _now);
    private static SharePayload Payload()
    {
        var run = new BenchmarkRun { Repetition = 1, Rendered = true, FpsAverage = 81.5, FpsOnePercentLow = 62,
            CpuMs = 4, GpuMs = 8, FrameMs = Distribution.Of([10, 12, 15]), ServerTickMs = Distribution.Of([4, 6, 8]),
            BotCount = 9, PerBotCpu = [new("SECRET-PILOT", 42.5)], Workers = [new(12345, 500, 1024)] };
        return SharePayload.From(new BenchmarkResult {
            Machine = "SECRET-HOSTNAME", Mode = "skirmish", Preset = "Small", DurationSeconds = 150,
            Runs = [run, run with { Repetition = 2 }, run with { Repetition = 3 }], Median = run,
            TestedSettings = new(), Recommendation = new(), Bottleneck = "Within measured budget"
        }, new("Example CPU", 4, 8, "Example GPU", 8, "1.2", 16, "Linux", "6.8", 1920, 1080));
    }
    private SharingState State() => JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(_dir, "sharing.json")), ShareJson.Default.SharingState)!;

    [Fact]
    public void Schema_round_trip_preserves_P729_metrics_and_excludes_identity()
    {
        var payload = Payload() with { InstallId = Guid.NewGuid() };
        string json = payload.ToJson();
        var restored = JsonSerializer.Deserialize(json, ShareJson.Default.SharePayload)!;
        Assert.Equal(json, restored.ToJson());
        Assert.Equal(81.5, restored.Result.Median.FpsAverage);
        Assert.Equal(62, restored.Result.Median.FpsOnePercentLow);
        Assert.Equal(12, restored.Result.Median.FrameMs!.P50);
        Assert.Equal(8, restored.Result.Median.ServerTickMs!.Worst);
        Assert.Equal(42.5, restored.Result.Median.BotDecisionCpuMs[0]);
        Assert.DoesNotContain("SECRET", json); Assert.DoesNotContain("pid", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("machine", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= 32768);
        // The collector's tests validate this actual source-generated .NET output, not a hand-written approximation.
        File.WriteAllText(Environment.GetEnvironmentVariable("BS1_PAYLOAD_PATH") ?? Path.Combine(Path.GetTempPath(), "bs1-client-payload.json"), json);
    }

    [Fact]
    public void Commit_metadata_only_accepts_hashes_and_never_shares_custom_build_paths()
    {
        Assert.Equal("abcdef123", SharePayload.CommitFromInformationalVersion("1.0.0+ABCDEF123"));
        foreach (string? version in new[] { null, "1.0.0", "1.0.0+user/private-branch", "1.0.0+user@host", @"1.0.0+C:\Users\someone", "1.0.0+123" })
            Assert.Equal("unknown", SharePayload.CommitFromInformationalVersion(version));
    }

    [Fact]
    public async Task Default_No_and_disabled_endpoint_save_locally_without_network_or_ID()
    {
        var transport = new Transport();
        using (var sharing = Create(transport))
        {
            await sharing.RecordAsync(Payload()); await sharing.PumpAsync();
            Assert.Null(sharing.Consent); Assert.Null(State().InstallId);
            Assert.Single(Directory.GetFiles(Path.Combine(_dir, "results")));
            Assert.Empty(State().Queue); Assert.Empty(transport.Calls);
        }
        using var disabled = Create(transport, "");
        Assert.False(disabled.Available);
        await disabled.SetConsentAsync(true); await disabled.RecordAsync(Payload()); await disabled.PumpAsync();
        Assert.False(disabled.Consent); Assert.Null(State().InstallId); Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task View_equals_exact_uploaded_JSON_and_opt_in_survives_restart()
    {
        var transport = new Transport(); string preview;
        using (var sharing = Create(transport))
        {
            await sharing.RecordAsync(Payload()); await sharing.SetConsentAsync(true);
            preview = sharing.Preview;
            Assert.NotNull(State().InstallId);
            Assert.Equal(preview, State().Queue.Single().Json);
            Assert.Equal(preview, sharing.Outgoing[0]);
        }
        using var restarted = Create(transport);
        await restarted.InitializeAsync(); Assert.True(restarted.Consent);
        await restarted.PumpAsync();
        Assert.Equal(preview, Assert.Single(transport.Calls).Json); Assert.Empty(State().Queue);
    }

    [Fact]
    public async Task Offline_backoff_is_durable_and_retries_only_when_due()
    {
        var transport = new Transport { Offline = true };
        using (var sharing = Create(transport))
        {
            await sharing.SetConsentAsync(true); await sharing.RecordAsync(Payload()); await sharing.PumpAsync();
            Assert.Equal(1, State().Queue.Single().Attempts);
            Assert.InRange(State().Queue.Single().NextAttempt, _now.AddSeconds(5), _now.AddSeconds(8));
            await sharing.PumpAsync(); Assert.Single(transport.Calls);
        }
        using var restarted = Create(transport);
        await restarted.PumpAsync(); Assert.Single(transport.Calls);
        _now = _now.AddSeconds(9); transport.Offline = false;
        await restarted.PumpAsync(); Assert.Equal(2, transport.Calls.Count); Assert.Empty(State().Queue);
    }

    [Fact]
    public async Task Opt_out_cancels_results_and_followups_and_is_remembered()
    {
        var transport = new Transport();
        using (var sharing = Create(transport))
        {
            await sharing.SetConsentAsync(true); await sharing.RecordAsync(Payload());
            await sharing.SetConsentAsync(false); await sharing.PumpAsync();
            await sharing.ObserveSettingsAsync(ShareSettings.From(new()), true);
            Assert.Empty(State().Queue); Assert.Empty(State().FollowUps); Assert.Empty(transport.Calls);
        }
        using var restarted = Create(transport); await restarted.InitializeAsync();
        Assert.False(restarted.Consent); await restarted.RecordAsync(Payload()); await restarted.PumpAsync();
        Assert.Empty(transport.Calls);
        await restarted.SetConsentAsync(true);
        Assert.Single(State().Queue); await restarted.PumpAsync();
        Assert.Single(transport.Calls); // Re-enabling explicitly shares the latest result again.
    }

    [Fact]
    public async Task Delete_is_durable_offline_rotates_ID_and_precedes_new_uploads()
    {
        var transport = new Transport { Offline = true };
        Guid? old;
        using (var sharing = Create(transport))
        {
            await sharing.SetConsentAsync(true); await sharing.RecordAsync(Payload()); old = State().InstallId;
            await sharing.DeleteAsync(); await sharing.PumpAsync();
            Assert.Null(State().InstallId); Assert.False(sharing.Consent);
            Assert.Equal("DELETE", State().Queue.Single().Method);
        }
        _now = _now.AddMinutes(1); transport.Offline = false;
        using var restarted = Create(transport);
        await restarted.SetConsentAsync(true); Assert.NotEqual(old, State().InstallId);
        await restarted.PumpAsync(); await restarted.PumpAsync();
        Assert.Equal(new[] { "DELETE", "DELETE", "POST" }, transport.Calls.Select(c => c.Method));
        Assert.EndsWith(old.ToString()!, transport.Calls[1].Url);
        Assert.Empty(State().Queue);
    }

    [Fact]
    public async Task Followup_after_three_sessions_or_seven_days_names_only_changed_settings()
    {
        var transport = new Transport(); using var sharing = Create(transport);
        await sharing.SetConsentAsync(true); await sharing.RecordAsync(Payload()); await sharing.PumpAsync();
        var changed = ShareSettings.From(new BenchmarkRecommendation { Bloom = true });
        await sharing.ObserveSettingsAsync(changed, true); await sharing.ObserveSettingsAsync(changed, true);
        Assert.Empty(State().Queue);
        await sharing.ObserveSettingsAsync(changed, true);
        Assert.Contains(State().Queue.Single().Json!, sharing.Outgoing);
        await sharing.PumpAsync();
        using var json = JsonDocument.Parse(transport.Calls[1].Json!);
        Assert.False(json.RootElement.GetProperty("kept").GetBoolean());
        Assert.Equal("bloom", json.RootElement.GetProperty("changedSettings")[0].GetString());
        await sharing.ObserveSettingsAsync(changed, true); Assert.Empty(State().Queue);
        await sharing.RecordAsync(Payload()); await sharing.PumpAsync(); _now = _now.AddDays(7);
        await sharing.ObserveSettingsAsync(ShareSettings.From(new())); await sharing.PumpAsync();
        using var kept = JsonDocument.Parse(transport.Calls[3].Json!);
        Assert.True(kept.RootElement.GetProperty("kept").GetBoolean());
        Assert.Empty(State().FollowUps);
    }

    [Fact]
    public async Task Deletion_waits_for_inflight_upload_and_leaves_no_later_old_upload()
    {
        var transport = new Transport { Entered = new(), Release = new() }; using var sharing = Create(transport);
        await sharing.SetConsentAsync(true); await sharing.RecordAsync(Payload());
        var pumping = sharing.PumpAsync(); await transport.Entered.Task;
        var deleting = sharing.DeleteAsync(); Assert.False(deleting.IsCompleted);
        transport.Release.TrySetResult(); await pumping; await deleting; await sharing.PumpAsync();
        Assert.Equal(new[] { "POST", "DELETE" }, transport.Calls.Select(c => c.Method));
        Assert.Empty(State().Queue); Assert.Null(State().InstallId);
    }

    [Fact]
    public async Task Real_loopback_HTTP_transport_uploads_exact_preview()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync();
            await using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            string? line; int length = 0;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Split(':')[1]);
            char[] body = new char[length]; int read = 0; while (read < length) { int n = await reader.ReadAsync(body.AsMemory(read)); if (n == 0) throw new EndOfStreamException(); read += n; }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 201 Created\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            return new string(body);
        });
        using var sharing = new BenchmarkSharing(_dir, $"http://127.0.0.1:{port}");
        await sharing.RecordAsync(Payload()); await sharing.SetConsentAsync(true); string preview = sharing.Preview;
        await sharing.PumpAsync(); Assert.Equal(preview, await server.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(State().Queue);
    }

    [Fact]
    public async Task Real_offline_transport_keeps_queue_with_short_timeout()
    {
        // This exercises real HTTP failure without requiring a configured loopback interface.
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var sharing = new BenchmarkSharing(_dir, "https://collector.invalid", http);
        await sharing.SetConsentAsync(true); await sharing.RecordAsync(Payload());
        var start = System.Diagnostics.Stopwatch.StartNew(); await sharing.PumpAsync();
        Assert.True(start.Elapsed < TimeSpan.FromSeconds(5)); Assert.Single(State().Queue);
        Assert.Equal(1, State().Queue[0].Attempts);
    }

    [Fact]
    public async Task Corrupt_state_fails_closed_and_still_archives_new_results()
    {
        Directory.CreateDirectory(_dir); await File.WriteAllTextAsync(Path.Combine(_dir, "sharing.json"), "{corrupt");
        var transport = new Transport(); using var sharing = Create(transport);
        await sharing.RecordAsync(Payload()); await sharing.PumpAsync();
        Assert.Null(sharing.Consent); Assert.Single(Directory.GetFiles(Path.Combine(_dir, "results"))); Assert.Empty(transport.Calls);
    }
    [Fact]
    public async Task Timeout_retains_payload_and_retries_without_waiting_forever()
    {
        var transport = new Transport { Release = new() }; using var sharing = Create(transport);
        await sharing.SetConsentAsync(true); await sharing.RecordAsync(Payload());
        var start = System.Diagnostics.Stopwatch.StartNew(); await sharing.PumpAsync();
        Assert.InRange(start.Elapsed.TotalSeconds, 1.5, 5);
        Assert.Equal(1, State().Queue.Single().Attempts);
        Assert.Equal(sharing.Preview, State().Queue.Single().Json);
    }

    [Fact]
    public async Task Collector_change_requires_consent_and_deletes_old_ID_before_new_upload()
    {
        var transport = new Transport(); Guid? old;
        using (var sharing = Create(transport))
        {
            await sharing.SetConsentAsync(true); await sharing.RecordAsync(Payload()); old = State().InstallId;
        }
        using var changed = Create(transport, "http://localhost:9999");
        await changed.InitializeAsync(); Assert.False(changed.Consent);
        await changed.RecordAsync(Payload()); await changed.PumpAsync(); Assert.Empty(transport.Calls);
        await changed.SetConsentAsync(true); Assert.NotEqual(old, State().InstallId);
        await changed.PumpAsync(); await changed.PumpAsync();
        Assert.StartsWith("http://localhost:8888/v1/install/", transport.Calls[0].Url);
        Assert.Equal("DELETE", transport.Calls[0].Method);
        Assert.StartsWith("http://localhost:9999/v1/results", transport.Calls[1].Url);
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}
