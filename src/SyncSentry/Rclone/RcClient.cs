using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SyncSentry.Outbox;
using SyncSentry.Security;

namespace SyncSentry.Rclone;

public sealed record RcTransfer(string Name, long Bytes, long Size, int? Percentage, double Speed);

public sealed record RcStats(
    long Bytes,
    long TotalBytes,
    long Files,
    long TotalFiles,
    double Speed,
    int? EtaSeconds,
    int Errors,
    string? LastError,
    IReadOnlyList<RcTransfer> Transferring);

public sealed record RcJob(
    long Id,
    string Group,
    bool Finished,
    bool Success,
    string? Error,
    DateTimeOffset? StartedUtc,
    RcStats? Stats);

public sealed record RcSnapshot(bool Reachable, string? Error, IReadOnlyList<RcJob> Jobs, RcStats? Global);

public sealed record RcStartRequest(
    string Operation,
    string Source,
    string Destination,
    string Group,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude);

public sealed record RcCallResult(bool Ok, bool AlreadyRunning, long? JobId, string? Error);

public interface IRcloneRc
{
    Task<RcSnapshot> GetSnapshotAsync(string? url, string? user, string? password, CancellationToken cancellationToken);

    Task<RcCallResult> StartAsync(string? url, string? user, string? password, RcStartRequest request, CancellationToken cancellationToken);

    Task<RemoteListResult> ListRemoteAsync(string? url, string? user, string? password, string remote, int maxFiles, CancellationToken cancellationToken);
}

public sealed record RemoteListResult(bool Ok, bool Truncated, string? Error, IReadOnlyList<FileFingerprint> Files);

public static class RcJson
{
    public static RcStats ParseStats(JsonElement root)
    {
        var bytes = Long(root, "bytes");
        var total = Long(root, "totalBytes");
        var files = Long(root, "transfers");
        var totalFiles = Long(root, "totalTransfers");
        var speed = Double(root, "speed");
        int? eta = null;
        if (root.TryGetProperty("eta", out var etaValue) && etaValue.ValueKind == JsonValueKind.Number && etaValue.TryGetInt32(out var etaSeconds) && etaSeconds >= 0)
            eta = etaSeconds;
        var errors = (int)Math.Min(int.MaxValue, Long(root, "errors"));
        var last = root.TryGetProperty("lastError", out var lastError) ? lastError.GetString() : null;
        if (string.IsNullOrWhiteSpace(last))
            last = null;
        var transferring = new List<RcTransfer>();
        if (root.TryGetProperty("transferring", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (transferring.Count >= 8)
                    break;
                var name = item.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? "" : "";
                int? pct = item.TryGetProperty("percentage", out var pctValue) && pctValue.TryGetInt32(out var parsed) ? parsed : null;
                transferring.Add(new RcTransfer(name, Long(item, "bytes"), Long(item, "size"), pct, Double(item, "speed")));
            }
        }

        return new RcStats(bytes, total, files, totalFiles, speed, eta, errors, SecretRedactor.ForDisplay(last, 240), transferring);
    }

    public static IReadOnlyList<long> ParseJobIds(JsonElement root)
    {
        if (!root.TryGetProperty("jobids", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        var ids = new List<long>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.TryGetInt64(out var id))
                ids.Add(id);
        }

        return ids;
    }

    public static RcJob ParseJob(JsonElement root, RcStats? stats)
    {
        var id = Long(root, "id");
        var group = root.TryGetProperty("group", out var groupValue) ? groupValue.GetString() ?? "" : "";
        var finished = root.TryGetProperty("finished", out var finishedValue) && finishedValue.ValueKind == JsonValueKind.True;
        var success = root.TryGetProperty("success", out var successValue) && successValue.ValueKind == JsonValueKind.True;
        var error = root.TryGetProperty("error", out var errorValue) ? errorValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(error))
            error = null;
        DateTimeOffset? started = null;
        if (root.TryGetProperty("startTime", out var start) && start.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(start.GetString(), out var parsed))
            started = parsed;
        return new RcJob(id, group, finished, success, SecretRedactor.ForDisplay(error, 240), started, stats);
    }

    public static long? ParseStartedJob(JsonElement root)
        => root.TryGetProperty("jobid", out var id) && id.TryGetInt64(out var value) ? value : null;

    private static long Long(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static double Double(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : 0;
}

public sealed class RcloneRcClient(IHttpClientFactory factory) : IRcloneRc
{
    public async Task<RcSnapshot> GetSnapshotAsync(string? url, string? user, string? password, CancellationToken cancellationToken)
    {
        if (!Rules.RcUrl(url))
            return new RcSnapshot(false, "rclone RC URL is not set.", [], null);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var listDoc = await PostAsync(url!, user, password, "job/list", new { }, timeout.Token).ConfigureAwait(false);
            if (listDoc is null)
                return new RcSnapshot(false, "rclone RC did not respond.", [], null);
            var ids = RcJson.ParseJobIds(listDoc.RootElement);
            var jobs = new List<RcJob>();
            foreach (var id in ids.Take(30))
            {
                using var status = await PostAsync(url!, user, password, "job/status", new { jobid = id }, timeout.Token).ConfigureAwait(false);
                if (status is null)
                    continue;
                var group = status.RootElement.TryGetProperty("group", out var groupValue) ? groupValue.GetString() : null;
                RcStats? stats = null;
                if (!string.IsNullOrWhiteSpace(group))
                {
                    using var statsDoc = await PostAsync(url!, user, password, "core/stats", new { group }, timeout.Token).ConfigureAwait(false);
                    if (statsDoc is not null)
                        stats = RcJson.ParseStats(statsDoc.RootElement);
                }

                jobs.Add(RcJson.ParseJob(status.RootElement, stats));
            }

            using var globalDoc = await PostAsync(url!, user, password, "core/stats", new { }, timeout.Token).ConfigureAwait(false);
            var global = globalDoc is null ? null : RcJson.ParseStats(globalDoc.RootElement);
            var unfinished = jobs.Any(job => !job.Finished);
            if (!unfinished && global is { } active && (active.Transferring.Count > 0 || active.Speed > 1))
            {
                jobs.Add(new RcJob(0, "global", false, false, null, null, active));
            }

            return new RcSnapshot(true, null, jobs, global);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new RcSnapshot(false, "rclone RC could not be reached.", [], null);
        }
    }

    public async Task<RcCallResult> StartAsync(string? url, string? user, string? password, RcStartRequest request, CancellationToken cancellationToken)
    {
        if (!Rules.RcUrl(url))
            return new RcCallResult(false, false, null, "rclone RC URL is not set.");
        if (request.Operation is not ("copy" or "sync"))
            return new RcCallResult(false, false, null, "Operation must be copy or sync.");
        if (string.IsNullOrWhiteSpace(request.Source) || string.IsNullOrWhiteSpace(request.Destination))
            return new RcCallResult(false, false, null, "Source and destination are required.");

        var snapshot = await GetSnapshotAsync(url, user, password, cancellationToken).ConfigureAwait(false);
        if (!snapshot.Reachable)
            return new RcCallResult(false, false, null, snapshot.Error);
        if (snapshot.Jobs.Any(job => !job.Finished && string.Equals(job.Group, request.Group, StringComparison.Ordinal)))
            return new RcCallResult(false, true, null, "That job is already running.");

        var payload = new Dictionary<string, object?>
        {
            ["srcFs"] = request.Source,
            ["dstFs"] = request.Destination,
            ["_async"] = true,
            ["_group"] = request.Group
        };
        if (request.Include.Count > 0 || request.Exclude.Count > 0)
        {
            payload["_filter"] = new Dictionary<string, object?>
            {
                ["IncludeRule"] = request.Include,
                ["ExcludeRule"] = request.Exclude
            };
        }

        try
        {
            using var doc = await PostAsync(url!, user, password, "sync/" + request.Operation, payload, cancellationToken).ConfigureAwait(false);
            if (doc is null)
                return new RcCallResult(false, false, null, "rclone RC did not accept the job.");
            var id = RcJson.ParseStartedJob(doc.RootElement);
            return id is null
                ? new RcCallResult(false, false, null, "rclone RC did not return a job id.")
                : new RcCallResult(true, false, id, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new RcCallResult(false, false, null, "rclone RC could not be reached.");
        }
    }

    public async Task<RemoteListResult> ListRemoteAsync(string? url, string? user, string? password, string remote, int maxFiles, CancellationToken cancellationToken)
    {
        if (!Rules.RcUrl(url))
            return new RemoteListResult(false, false, "rclone RC URL is not set.", []);
        if (!Rules.TrySplitRemote(remote, out var fs, out var path))
            return new RemoteListResult(false, false, "Destination is not an rclone remote like name:path.", []);

        try
        {
            using var doc = await PostAsync(url!, user, password, "operations/list", new
            {
                fs,
                remote = path,
                opt = new { recurse = true, filesOnly = true, noMimeType = true }
            }, cancellationToken).ConfigureAwait(false);
            if (doc is null)
                return new RemoteListResult(false, false, "rclone RC did not list the remote.", []);
            if (!doc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                return new RemoteListResult(true, false, null, []);
            var files = new List<FileFingerprint>();
            var truncated = false;
            foreach (var item in list.EnumerateArray())
            {
                if (item.TryGetProperty("IsDir", out var dir) && dir.ValueKind == JsonValueKind.True)
                    continue;
                if (files.Count >= maxFiles)
                {
                    truncated = true;
                    break;
                }

                var rel = item.TryGetProperty("Path", out var pathValue) ? pathValue.GetString() ?? "" : "";
                var size = item.TryGetProperty("Size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;
                long mtime = 0;
                if (item.TryGetProperty("ModTime", out var mod) && mod.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(mod.GetString(), out var stamp))
                    mtime = stamp.ToUnixTimeSeconds();
                if (rel.Length == 0)
                    continue;
                files.Add(new FileFingerprint(OutboxComparer.Normalize(rel), size, mtime));
            }

            return new RemoteListResult(true, truncated, null, files);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new RemoteListResult(false, false, "rclone RC could not list the remote.", []);
        }
    }

    private async Task<JsonDocument?> PostAsync(string url, string? user, string? password, string method, object payload, CancellationToken cancellationToken)
    {
        var client = factory.CreateClient("rclone");
        var endpoint = url.TrimEnd('/') + "/" + method;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(user))
        {
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + (password ?? "")));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        var length = response.Content.Headers.ContentLength;
        if (length > 48_000_000)
            throw new InvalidOperationException("response too large");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
            if (total > 48_000_000)
                throw new InvalidOperationException("response too large");
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
