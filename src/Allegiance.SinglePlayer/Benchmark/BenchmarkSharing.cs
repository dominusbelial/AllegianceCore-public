using System.Net;
using System.Text;
using System.Text.Json;

namespace Allegiance.SinglePlayer.Benchmark;

public sealed record ShareJob(string Method, string Url, string? Json, int Attempts = 0, DateTimeOffset NextAttempt = default);
public sealed record FollowUp(Guid ResultId, Guid InstallId, DateTimeOffset Created, int Sessions, ShareSettings Suggested);
public sealed record SharingState
{
    public string? CollectorUrl { get; init; }
    public bool? Consent { get; init; }
    public Guid? InstallId { get; init; }
    public SharePayload? Last { get; init; }
    public List<ShareJob> Queue { get; init; } = [];
    public List<FollowUp> FollowUps { get; init; } = [];
}

/// <summary>All disk/network work runs off the game thread. One gate orders uploads, opt-out and deletion.</summary>
public sealed class BenchmarkSharing : IDisposable
{
    // Set only after deployment. An explicit empty config overrides this default.
    public const string DefaultCollectorUrl = "";
    private readonly string _directory, _endpoint;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _stop = new();
    private SharingState _state = new();
    private bool _loaded;
    private volatile string _preview = "No benchmark recorded yet.";
    private volatile string[] _outgoing = [];
    public IReadOnlyList<string> Outgoing => _outgoing;
    private volatile string _status = "Sharing: No (default)";
    private volatile Box _consent = new(null);
    private sealed record Box(bool? Value);
    public bool Available => _endpoint.Length > 0;
    public string Preview => _preview;
    public string Status => _status;
    public bool? Consent => _consent.Value;
    public BenchmarkSharing(string directory, string? collectorUrl = null, HttpClient? http = null, Func<DateTimeOffset>? now = null)
    {
        _directory = directory;
        string url = collectorUrl ?? DefaultCollectorUrl;
        _endpoint = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback)
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            ? url.TrimEnd('/') : "";
        _ownsHttp = http is null; _http = http ?? new HttpClient(); _now = now ?? (() => DateTimeOffset.UtcNow);
    }
    // Exposes a task for tests; callers in Game1 dispatch these methods through Task.Run.
    private async Task Locked(Func<Task> action)
    {
        try
        {
            await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
            try
            {
                if (!_loaded)
                {
                    string path = Path.Combine(_directory, "sharing.json");
                    if (File.Exists(path))
                    {
                        try { _state = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, _stop.Token).ConfigureAwait(false), ShareJson.Default.SharingState) ?? new(); }
                        catch (JsonException) { _state = new(); } // A corrupt file can never enable sharing.
                    }
                    _loaded = true; Refresh();
                }
                await action().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException or HttpRequestException)
        { _status = "Sharing paused; local data retained. Retry later."; }
    }
    private void Refresh()
    {
        bool changedCollector = _state.CollectorUrl is { } previous && previous != _endpoint;
        _consent = new(!Available || changedCollector ? false : _state.Consent);
        _preview = _state.Last?.ToJson() ?? "No benchmark recorded yet.";
        _outgoing = new[] { _preview }.Concat(_state.Queue.Where(j => j.Json is not null).Select(j => j.Json!)).Distinct(StringComparer.Ordinal).ToArray();
        _status = $"Sharing: {(_consent.Value == true ? "Yes" : "No")}; {_state.Queue.Count} queued";
    }
    private async Task Save()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "sharing.json");
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(_state, ShareJson.Default.SharingState), _stop.Token).ConfigureAwait(false);
        File.Move(path + ".tmp", path, true); Refresh();
    }
    public Task InitializeAsync() => Locked(() => Task.CompletedTask);
    public Task RecordAsync(SharePayload payload) => Locked(async () =>
    {
        payload = payload with { InstallId = _state.Consent == true && Available && _state.CollectorUrl == _endpoint ? _state.InstallId : null };
        _state = _state with { Last = payload };
        // Always retain the privacy-filtered result locally, including with networking/config disabled.
        Directory.CreateDirectory(Path.Combine(_directory, "results"));
        await File.WriteAllTextAsync(Path.Combine(_directory, "results", payload.EventId + ".json"), payload.ToJson(), _stop.Token).ConfigureAwait(false);
        if (Available && _state.Consent == true) EnqueueResult(payload);
        await Save().ConfigureAwait(false);
    });
    private void EnqueueResult(SharePayload payload)
    {
        if (payload.InstallId is not { } id || Encoding.UTF8.GetByteCount(payload.ToJson()) > 32768) return;
        _state.Queue.Add(new("POST", _endpoint + "/v1/results", payload.ToJson()));
        _state.FollowUps.Add(new(payload.EventId, id, _now(), 0, payload.Result.SettingsSuggested));
    }
    public Task SetConsentAsync(bool enabled) => Locked(async () =>
    {
        enabled &= Available;
        if (enabled && _state.CollectorUrl is { } oldUrl && oldUrl != _endpoint)
        {
            _state.Queue.RemoveAll(j => j.Method != "DELETE"); _state.FollowUps.Clear();
            if (_state.InstallId is { } oldId) _state.Queue.Add(new("DELETE", oldUrl + "/v1/install/" + oldId, null));
            _state = _state with { InstallId = null, Last = _state.Last is { } oldLast ? oldLast with { InstallId = null } : null };
        }
        _state = _state with { Consent = enabled, CollectorUrl = enabled ? _endpoint : _state.CollectorUrl, InstallId = enabled ? _state.InstallId ?? Guid.NewGuid() : _state.InstallId };
        if (!enabled)
        {
            _state.Queue.RemoveAll(j => j.Method != "DELETE"); _state.FollowUps.Clear();
            _state = _state with { Last = _state.Last is { } last ? last with { InstallId = null } : null };
        }
        else if (_state.Last is { InstallId: null } last)
        {
            last = last with { InstallId = _state.InstallId };
            _state = _state with { Last = last }; EnqueueResult(last);
        }
        await Save().ConfigureAwait(false);
    });
    // Delete is a durable job even after opt-out. Rotation cannot race an older in-flight upload.
    public Task DeleteAsync() => Locked(async () =>
    {
        _state.Queue.RemoveAll(j => j.Method != "DELETE"); _state.FollowUps.Clear();
        if (_state.CollectorUrl is { } collector && _state.InstallId is { } id) _state.Queue.Add(new("DELETE", collector + "/v1/install/" + id, null));
        _state = _state with { Consent = false, InstallId = null, Last = _state.Last is { } last ? last with { InstallId = null } : null };
        await Save().ConfigureAwait(false);
    });
    public Task ObserveSettingsAsync(ShareSettings settings, bool session = false) => Locked(async () =>
    {
        if (!Available || _state.Consent != true || _state.CollectorUrl != _endpoint) return;
        foreach (var follow in _state.FollowUps.ToArray())
        {
            var next = follow with { Sessions = follow.Sessions + (session ? 1 : 0) };
            _state.FollowUps.Remove(follow);
            if (next.Sessions >= 3 || _now() - next.Created >= TimeSpan.FromDays(7))
            {
                string[] changed = settings.ChangedFrom(next.Suggested);
                var payload = new KeptPayload(SharePayload.CurrentSchema, Guid.NewGuid(), next.InstallId, next.ResultId, _now(), changed.Length == 0, changed);
                _state.Queue.Add(new("POST", _endpoint + "/v1/kept", JsonSerializer.Serialize(payload, ShareJson.Default.KeptPayload)));
            }
            else _state.FollowUps.Add(next);
        }
        await Save().ConfigureAwait(false);
    });
    public Task PumpAsync() => Locked(async () =>
    {
        if (!Available) return;
        // One bounded request per pump; never busy-loop when offline. Preserve FIFO for result -> kept -> delete.
        var job = _state.Queue.FirstOrDefault();
        if (job is null || job.NextAttempt > _now()) return;
        if (job.Method != "DELETE" && _state.Consent != true) return;
        // A changed endpoint cannot redirect an existing install's data to another service.
        if (job.Method != "DELETE" && !job.Url.StartsWith(_endpoint + "/v1/", StringComparison.Ordinal)) { _status = "Queue belongs to a different collector; restore its URL to delete or upload."; return; }
        bool done = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var request = new HttpRequestMessage(new HttpMethod(job.Method), job.Url);
            if (job.Json is { } json) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            done = response.IsSuccessStatusCode || (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException) { }
        _state.Queue.RemoveAt(0);
        if (!done)
        {
            int attempts = Math.Min(job.Attempts + 1, 20);
            _state.Queue.Insert(0, job with { Attempts = attempts, NextAttempt = _now().AddSeconds(Math.Min(21600, 5 * Math.Pow(2, attempts - 1)) + Random.Shared.NextDouble() * 3) });
        }
        await Save().ConfigureAwait(false);
    });
    public void Dispose() { _stop.Cancel(); if (_ownsHttp) _http.Dispose(); /* No waiting on network or disk on the game thread. */ }
}
