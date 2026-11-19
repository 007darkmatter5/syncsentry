namespace SyncSentry.Data;

public sealed class InstanceSetting
{
    public int Id { get; set; }
    public string RcUrl { get; set; } = "http://127.0.0.1:5572";
    public string RcUser { get; set; } = "";
    public string RcPasswordProtected { get; set; } = "";
    public string NtfyServerUrl { get; set; } = "";
    public string NtfyTopic { get; set; } = "";
    public string NtfyTokenProtected { get; set; } = "";
    public bool AlertJobFailed { get; set; } = true;
    public bool AlertJobCompleted { get; set; }
    public bool AlertMountUnresponsive { get; set; } = true;
    public bool AlertMountUnmounted { get; set; } = true;
    public bool AlertAcceptRoutesOff { get; set; } = true;
    public int ProbeTimeoutSeconds { get; set; } = 3;
    public int PollSeconds { get; set; } = 20;
    public int OutboxCacheMinutes { get; set; } = 15;
    public int LogStaleSeconds { get; set; } = 150;
    public int ScanMaxFiles { get; set; } = 200_000;
    public int ScanSeconds { get; set; } = 90;
    public string TailscaleStatusPath { get; set; } = "/config/host/tailscale.json";
    public string TailscaleSocketPath { get; set; } = "";
    public string TriggerDirectory { get; set; } = "/config/triggers";
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class WatchedMount
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class LogSource
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class SyncJobDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string StartMode { get; set; } = "trigger";
    public string Operation { get; set; } = "copy";
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string ScriptKey { get; set; } = "";
    public string IncludeRules { get; set; } = "";
    public string ExcludeRules { get; set; } = "";
    public int? LogSourceId { get; set; }
    public LogSource? LogSource { get; set; }
}

public sealed class JobRun
{
    public long Id { get; set; }
    public Guid? JobDefinitionId { get; set; }
    public string Name { get; set; } = "";
    public string Origin { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Status { get; set; } = "running";
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public long FilesTransferred { get; set; }
    public long TotalFiles { get; set; }
    public double SpeedBytesPerSecond { get; set; }
    public int? EtaSeconds { get; set; }
    public int ErrorCount { get; set; }
    public string? LastError { get; set; }
    public string? Transferring { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class OutboxPair
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string Destination { get; set; } = "";
    public string DestinationKind { get; set; } = "path";
    public bool Enabled { get; set; } = true;
    public bool ForceRefresh { get; set; }
    public DateTimeOffset? LastScanUtc { get; set; }
    public int PendingCount { get; set; }
    public long PendingBytes { get; set; }
    public bool Truncated { get; set; }
    public string? Error { get; set; }
    public string SampleJson { get; set; } = "[]";
    public string Status { get; set; } = "idle";
}

public sealed class AlertEvent
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Delivered { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class MountStateRow
{
    public int Id { get; set; }
    public string Path { get; set; } = "";
    public string State { get; set; } = "unknown";
    public DateTimeOffset SinceUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class RouteStateRow
{
    public int Id { get; set; }
    public bool? AcceptRoutes { get; set; }
    public DateTimeOffset SinceUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}
