namespace SyncSentry.Alerts;

public sealed record AlertToggles(
    bool JobFailed,
    bool JobCompleted,
    bool MountUnresponsive,
    bool MountUnmounted,
    bool AcceptRoutesOff);

public sealed record AlertMessage(string Key, string Kind, string Title, string Body);

public static class AlertPolicy
{
    public static IReadOnlyList<AlertMessage> ForMount(
        AlertToggles toggles,
        string name,
        string path,
        string? previous,
        string current,
        DateTimeOffset since)
    {
        if (string.Equals(previous, current, StringComparison.Ordinal))
            return [];
        if (previous is null)
            return [];

        if (current == "unresponsive" && toggles.MountUnresponsive)
        {
            return
            [
                new AlertMessage(
                    $"mount-unresponsive:{path}:{since:O}",
                    "mount-unresponsive",
                    $"Mount unresponsive: {name}",
                    $"{name} ({path}) stopped answering. A hard CIFS mount can freeze when the network path drops. SyncSentry stopped waiting on it.")
            ];
        }

        if (current == "unmounted" && toggles.MountUnmounted)
        {
            return
            [
                new AlertMessage(
                    $"mount-unmounted:{path}:{since:O}",
                    "mount-unmounted",
                    $"Mount unmounted: {name}",
                    $"{name} ({path}) is not mounted.")
            ];
        }

        return [];
    }

    public static IReadOnlyList<AlertMessage> ForRoutes(AlertToggles toggles, bool? previous, bool? current, DateTimeOffset since)
    {
        if (!toggles.AcceptRoutesOff || current != false || previous == false)
            return [];
        return
        [
            new AlertMessage(
                $"accept-routes:{since:O}",
                "accept-routes",
                "Tailscale accept-routes is off",
                "Tailscale is not accepting subnet routes. The UNAS path via 192.168.1.0/24 may be unreachable.")
        ];
    }

    public static IReadOnlyList<AlertMessage> ForJob(
        AlertToggles toggles,
        string name,
        string? previousStatus,
        string currentStatus,
        string identity,
        DateTimeOffset since)
    {
        if (!string.Equals(previousStatus, "running", StringComparison.Ordinal))
            return [];
        if (currentStatus == "failed" && toggles.JobFailed)
        {
            return
            [
                new AlertMessage(
                    $"job-failed:{identity}:{since:O}",
                    "job-failed",
                    $"Job failed: {name}",
                    $"{name} finished with errors.")
            ];
        }

        if (currentStatus == "completed" && toggles.JobCompleted)
        {
            return
            [
                new AlertMessage(
                    $"job-completed:{identity}:{since:O}",
                    "job-completed",
                    $"Job completed: {name}",
                    $"{name} finished.")
            ];
        }

        return [];
    }
}
