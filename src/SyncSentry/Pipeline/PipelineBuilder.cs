namespace SyncSentry.Pipeline;

public sealed record SourceLink(bool Configured, string? Problem);

public sealed record PipelineSources(SourceLink Sonarr, SourceLink Radarr, SourceLink Lidarr, SourceLink Plex);

public sealed record PipelineHint(string Text, bool Settings);

public sealed record StageMark(string Name, string State, DateTimeOffset? At, string? Note);

public sealed record PipelineItem(
    string Key,
    string Title,
    string Subtitle,
    string Badge,
    string BadgeText,
    string? Problem,
    string? Elapsed,
    IReadOnlyList<StageMark> Stages,
    DateTimeOffset SortTime);

public sealed record PipelineInput(
    DateTimeOffset Now,
    int WindowDays,
    IReadOnlyList<PushLine> Lines,
    IReadOnlyList<ArrItem> Arr,
    IReadOnlyList<PlexHit> Plex,
    PipelineSources Sources,
    IReadOnlySet<string> OnDisk,
    bool WorkerLogMissing,
    bool HourlyLogMissing,
    string? LogProblem = null);

public sealed record PipelineSnapshot(
    DateTimeOffset GeneratedUtc,
    int WindowDays,
    bool Waiting,
    IReadOnlyList<PipelineItem> Items,
    IReadOnlyList<PipelineHint> Hints)
{
    public static PipelineSnapshot Empty(int days) => new(DateTimeOffset.MinValue, days, true, [], []);
}

public static class PipelineBuilder
{
    public static PipelineSnapshot Build(PipelineInput input)
    {
        var days = Math.Clamp(input.WindowDays, 1, PipelinePaths.MaxWindowDays);
        var folders = UnasPushLog.Fold(input.Lines);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<PipelineItem>();
        foreach (var arr in input.Arr)
        {
            var folder = MatchFolder(arr, folders);
            if (folder is not null)
                used.Add(FolderKey.DiskKey(folder.LibraryTag, folder.RelativeFolder));
            items.Add(FromArr(arr, folder, input));
        }

        foreach (var folder in folders)
        {
            var key = FolderKey.DiskKey(folder.LibraryTag, folder.RelativeFolder);
            if (used.Contains(key) || folder.RelativeFolder.Length == 0)
                continue;
            // A scan request, a run summary, or an already-in-sync note is not its own item.
            // It only fills a stage on a movie, episode, album, or a folder that was copied or failed.
            if (folder.CopiedUtc is null && !folder.Failed)
                continue;
            items.Add(FromFolder(folder, input));
        }

        var since = input.Now - TimeSpan.FromDays(days);
        var visible = items
            .Where(item => item.SortTime >= since)
            .OrderBy(item => Rank(item.Badge))
            .ThenByDescending(item => item.SortTime)
            .ToList();
        return new PipelineSnapshot(input.Now, days, false, visible, Hints(input));
    }

    private static PipelineItem FromArr(ArrItem arr, PushFolder? folder, PipelineInput input)
    {
        var link = LinkFor(arr.App, input.Sources);
        var copied = CopyOf(folder, arr.ImportedUtc, OnDisk(arr, input));
        var scanAt = ScanOf(folder, arr.ImportedUtc);
        var relative = arr.RelativeFolder.Length > 0 ? arr.RelativeFolder : copied.Relative;
        var plex = MatchPlex(arr.LibraryTag, relative, arr.ExternalId, arr.SeriesGuid, arr.AlsoGuid, arr.Season, arr.Episode, input.Plex);
        var logsKnown = !input.WorkerLogMissing || !input.HourlyLogMissing;
        var stages = new List<StageMark>
        {
            DownloadStage(arr),
            ImportStage(arr, link, input.Now),
            CopyStage(copied, arr.ImportedUtc, input.Now, logsKnown),
            ScanStage(scanAt, logsKnown),
            PlexStage(plex, input)
        };
        return Finish(
            arr.App + "|" + arr.Identity,
            arr.Title,
            arr.LibraryFolder,
            KindLabel(arr.Kind),
            arr.App,
            copied.Bytes,
            stages,
            input.Now);
    }

    private static PipelineItem FromFolder(PushFolder folder, PipelineInput input)
    {
        var app = MediaLibrary.App(folder.LibraryTag);
        var link = LinkFor(app, input.Sources);
        var copied = CopyOf(folder, null, OnDiskFolder(folder, input));
        var scanAt = folder.ScanUtc;
        var plex = MatchPlex(folder.LibraryTag, folder.RelativeFolder, null, null, null, null, null, input.Plex);
        var looked = link.Configured && link.Problem is null;
        var stages = new List<StageMark>
        {
            new("Downloaded", looked ? "pending" : "unknown", null, null),
            new("Imported", looked ? "pending" : "unknown", null, null),
            CopyStage(copied, null, input.Now, logsKnown: true),
            ScanStage(scanAt, logsKnown: true),
            PlexStage(plex, input)
        };
        return Finish(
            "log|" + FolderKey.DiskKey(folder.LibraryTag, folder.RelativeFolder),
            folder.RelativeFolder.Replace("/", " / ", StringComparison.Ordinal),
            folder.LibraryFolder.Length > 0 ? folder.LibraryFolder : MediaLibrary.Folder(folder.LibraryTag),
            "Folder",
            "push log",
            copied.Bytes,
            stages,
            input.Now);
    }

    private static StageMark DownloadStage(ArrItem arr)
    {
        if (arr.DownloadFailed && arr.ImportedUtc is null)
            return new StageMark("Downloaded", "failed", arr.GrabbedUtc, arr.Failure);
        if (arr.GrabbedUtc is not null)
            return new StageMark("Downloaded", "done", arr.GrabbedUtc, null);
        if (arr.ImportedUtc is not null)
            return new StageMark("Downloaded", "done", arr.ImportedUtc, null);
        return new StageMark("Downloaded", "pending", null, null);
    }

    private static StageMark ImportStage(ArrItem arr, SourceLink link, DateTimeOffset now)
    {
        if (arr.ImportedUtc is not null || arr.HasFile)
            return new StageMark("Imported", "done", arr.ImportedUtc, arr.ImportedUtc is null && arr.HasFile ? "Has a file" : null);
        if (!link.Configured || link.Problem is not null)
            return new StageMark("Imported", "unknown", null, null);
        if (arr.Dismissed || arr.DownloadSettled || arr.DownloadFailed)
            return new StageMark("Imported", "pending", null, null);
        if (arr.GrabbedUtc is not null && arr.InQueue == true && now - arr.GrabbedUtc.Value >= PipelinePaths.StuckAfter)
            return new StageMark("Imported", "stuck", null, null);
        return new StageMark("Imported", "pending", null, null);
    }

    private static StageMark CopyStage(CopyInfo copied, DateTimeOffset? imported, DateTimeOffset now, bool logsKnown)
    {
        if (copied.Failed && !copied.Reached)
            return new StageMark("Copied", "failed", copied.FailureUtc, copied.Failure);
        if (copied.Reached)
            return new StageMark("Copied", "done", copied.At, copied.At is null ? "On disk" : null);
        if (!logsKnown)
            return new StageMark("Copied", "unknown", null, null);
        if (imported is not null && now - imported.Value >= PipelinePaths.StuckAfter)
            return new StageMark("Copied", "stuck", null, null);
        return new StageMark("Copied", "pending", null, null);
    }

    private static StageMark ScanStage(DateTimeOffset? scanAt, bool logsKnown)
    {
        if (scanAt is not null)
            return new StageMark("Scan asked", "done", scanAt, null);
        if (!logsKnown)
            return new StageMark("Scan asked", "unknown", null, null);
        return new StageMark("Scan asked", "pending", null, null);
    }

    private static StageMark PlexStage(PlexHit? hit, PipelineInput input)
    {
        var link = input.Sources.Plex;
        if (!link.Configured || link.Problem is not null)
            return new StageMark("In Plex", "unknown", null, null);
        if (hit is not null)
            return new StageMark("In Plex", "done", hit.AddedUtc, null);
        return new StageMark("In Plex", "pending", null, null);
    }

    private static PipelineItem Finish(
        string key,
        string title,
        string library,
        string kind,
        string source,
        long? bytes,
        IReadOnlyList<StageMark> stages,
        DateTimeOffset now)
    {
        var badge = BadgeFor(stages);
        var problem = ProblemFor(stages, now);
        var times = stages.Where(stage => stage.At is not null).Select(stage => stage.At!.Value).ToList();
        var plexAt = stages[^1].State == "done" ? stages[^1].At : null;
        string? elapsed = null;
        if (times.Count > 0)
        {
            var end = plexAt ?? now;
            var span = end - times.Min();
            if (span < TimeSpan.Zero)
                span = TimeSpan.Zero;
            var text = SpanText(span);
            elapsed = plexAt is not null ? "Reached Plex in " + text : text + " so far";
        }

        var bits = new List<string> { library, kind, source };
        if (bytes is > 0)
            bits.Add(Text.TextFormat.Bytes(bytes.Value));
        if (elapsed is not null)
            bits.Add(elapsed);
        var sort = times.Count == 0 ? now : times.Max();
        return new PipelineItem(key, title, string.Join(" · ", bits), badge, BadgeText(badge), problem, elapsed, stages, sort);
    }

    private static string BadgeFor(IReadOnlyList<StageMark> stages)
    {
        if (stages.Any(stage => stage.State == "failed"))
            return "failed";
        if (stages.Any(stage => stage.State == "stuck"))
            return "stuck";
        if (stages[^1].State == "done")
            return "in-plex";
        for (var i = stages.Count - 1; i >= 0; i--)
        {
            if (stages[i].State == "done")
                return StageBadge(stages[i].Name);
        }

        if (stages.Any(stage => stage.State == "unknown"))
            return "unknown";
        return "pending";
    }

    private static string StageBadge(string name) => name switch
    {
        "Downloaded" => "downloaded",
        "Imported" => "imported",
        "Copied" => "copied",
        "Scan asked" => "scan",
        _ => "pending"
    };

    private static string BadgeText(string badge) => badge switch
    {
        "failed" => "Failed",
        "stuck" => "Stuck",
        "in-plex" => "In Plex",
        "scan" => "Scan asked",
        "copied" => "Copied",
        "imported" => "Imported",
        "downloaded" => "Downloaded",
        "unknown" => "Unknown",
        _ => "Waiting"
    };

    private static string? ProblemFor(IReadOnlyList<StageMark> stages, DateTimeOffset now)
    {
        var failed = stages.FirstOrDefault(stage => stage.State == "failed");
        if (failed is not null)
        {
            if (!string.IsNullOrWhiteSpace(failed.Note) && failed.Name != "Downloaded")
                return failed.Note;
            if (failed.Name == "Downloaded")
                return string.IsNullOrWhiteSpace(failed.Note) ? "The download failed." : failed.Note;
            return "The copy to the UNAS failed.";
        }

        var stuck = stages.FirstOrDefault(stage => stage.State == "stuck");
        if (stuck is null)
            return null;
        if (stuck.Name == "Copied")
        {
            var imported = stages.FirstOrDefault(stage => stage.Name == "Imported")?.At;
            var ago = imported is null ? "a while" : Ago(now - imported.Value);
            return "Imported " + ago + " ago and not copied yet.";
        }

        if (stuck.Name == "In Plex")
        {
            var copied = stages.FirstOrDefault(stage => stage.Name == "Copied")?.At;
            var ago = copied is null ? "a while" : Ago(now - copied.Value);
            return "Copied " + ago + " ago and not in Plex yet.";
        }

        if (stuck.Name == "Imported")
        {
            var downloaded = stages.FirstOrDefault(stage => stage.Name == "Downloaded")?.At;
            var ago = downloaded is null ? "a while" : Ago(now - downloaded.Value);
            return "Downloaded " + ago + " ago and not imported yet.";
        }

        return "This has not moved in a while.";
    }

    private static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        return SpanText(span);
    }

    private static string SpanText(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return span.Hours == 0 ? (int)span.TotalDays + "d" : (int)span.TotalDays + "d " + span.Hours + "h";
        if (span.TotalHours >= 1)
            return span.Minutes == 0 ? (int)span.TotalHours + "h" : (int)span.TotalHours + "h " + span.Minutes + "m";
        if (span.TotalMinutes >= 1)
            return span.Seconds == 0 ? (int)span.TotalMinutes + "m" : (int)span.TotalMinutes + "m " + span.Seconds + "s";
        return span.Seconds + "s";
    }

    private static CopyInfo CopyOf(PushFolder? folder, DateTimeOffset? imported, bool onDisk)
    {
        var copied = folder is not null && folder.CopiedUtc is not null && Covers(folder.CopiedUtc, imported);
        var failed = folder is not null && folder.Failed && (folder.FailureUtc is null || Covers(folder.FailureUtc, imported))
            && !(copied && folder.CopiedUtc > folder.FailureUtc);
        var at = copied ? folder!.CopiedUtc : null;
        var reached = copied || (onDisk && !failed);
        return new CopyInfo(reached, at, failed, folder?.FailureUtc, folder?.Failure, folder?.Bytes, folder?.RelativeFolder ?? "");
    }

    private static DateTimeOffset? ScanOf(PushFolder? folder, DateTimeOffset? imported)
    {
        if (folder?.ScanUtc is null)
            return null;
        return Covers(folder.ScanUtc, imported) ? folder.ScanUtc : null;
    }

    private static bool Covers(DateTimeOffset? when, DateTimeOffset? imported)
    {
        if (when is null)
            return false;
        if (imported is null)
            return true;
        return when.Value >= imported.Value - TimeSpan.FromMinutes(2);
    }

    private static bool OnDisk(ArrItem arr, PipelineInput input)
    {
        if (!string.IsNullOrEmpty(arr.RelativeFile) && input.OnDisk.Contains(FolderKey.DiskKey(arr.LibraryTag, arr.RelativeFile)))
            return true;
        return arr.RelativeFolder.Length > 0 && input.OnDisk.Contains(FolderKey.DiskKey(arr.LibraryTag, arr.RelativeFolder));
    }

    private static bool OnDiskFolder(PushFolder folder, PipelineInput input)
        => input.OnDisk.Contains(FolderKey.DiskKey(folder.LibraryTag, folder.RelativeFolder));

    private static PushFolder? MatchFolder(ArrItem arr, IReadOnlyList<PushFolder> folders)
    {
        if (arr.RelativeFolder.Length == 0)
            return null;
        return folders.FirstOrDefault(folder =>
            MediaLibrary.Same(folder.LibraryTag, arr.LibraryTag)
            && FolderKey.Matches(arr.RelativeFolder, folder.RelativeFolder));
    }

    private static PlexHit? MatchPlex(
        string libraryTag,
        string relative,
        string? externalId,
        string? seriesGuid,
        string? alsoGuid,
        int? season,
        int? episode,
        IReadOnlyList<PlexHit> hits)
    {
        if (season is int seasonNo && episode is int episodeNo)
        {
            var byNumber = hits.FirstOrDefault(hit =>
                hit.Kind == "episode"
                && hit.Season == seasonNo
                && hit.Episode == episodeNo
                && ShowMatches(hit, seriesGuid, alsoGuid));
            if (byNumber is not null)
                return byNumber;
        }

        foreach (var id in new[] { externalId, seriesGuid, alsoGuid })
        {
            if (string.IsNullOrEmpty(id))
                continue;
            var byId = hits.FirstOrDefault(hit =>
                hit.Kind is not ("episode" or "show")
                && hit.ExternalIds.Any(candidate => candidate.Equals(id, StringComparison.OrdinalIgnoreCase)));
            if (byId is not null)
                return byId;
        }

        if (relative.Length == 0)
            return null;
        return hits.FirstOrDefault(hit =>
            hit.Kind != "show"
            && MediaLibrary.Same(hit.LibraryFolder, libraryTag)
            && FolderKey.Matches(relative, hit.RelativeFolder)
            && (season is null || episode is null || hit.Season is null || (hit.Season == season && hit.Episode == episode)));
    }

    private static bool ShowMatches(PlexHit hit, string? seriesGuid, string? alsoGuid)
    {
        if (string.IsNullOrEmpty(seriesGuid) && string.IsNullOrEmpty(alsoGuid))
            return true;
        return hit.ExternalIds.Any(id =>
            id.Equals(seriesGuid, StringComparison.OrdinalIgnoreCase)
            || id.Equals(alsoGuid, StringComparison.OrdinalIgnoreCase));
    }

    private static SourceLink LinkFor(string app, PipelineSources sources) => app switch
    {
        "Sonarr" => sources.Sonarr,
        "Radarr" => sources.Radarr,
        "Lidarr" => sources.Lidarr,
        _ => new SourceLink(false, null)
    };

    private static List<PipelineHint> Hints(PipelineInput input)
    {
        var hints = new List<PipelineHint>();
        var missing = new List<string>();
        if (!input.Sources.Sonarr.Configured)
            missing.Add("Sonarr");
        if (!input.Sources.Radarr.Configured)
            missing.Add("Radarr");
        if (!input.Sources.Lidarr.Configured)
            missing.Add("Lidarr");
        if (missing.Count == 1)
            hints.Add(new PipelineHint(missing[0] + " is not set up. Downloads and imports from " + missing[0] + " stay unknown until you add the URL and API key under Settings.", true));
        else if (missing.Count > 1)
            hints.Add(new PipelineHint(JoinNames(missing) + " are not set up. Downloads and imports from them stay unknown until you add each URL and API key under Settings.", true));

        HintProblem(hints, "Sonarr", input.Sources.Sonarr);
        HintProblem(hints, "Radarr", input.Sources.Radarr);
        HintProblem(hints, "Lidarr", input.Sources.Lidarr);
        if (!input.Sources.Plex.Configured)
            hints.Add(new PipelineHint("Plex is not set up. The last stage stays unknown until you add the Plex URL and token under Settings.", true));
        else
            HintProblem(hints, "Plex", input.Sources.Plex);

        if (!string.IsNullOrEmpty(input.LogProblem))
            hints.Add(new PipelineHint(input.LogProblem, false));
        else if (input.WorkerLogMissing && input.HourlyLogMissing)
            hints.Add(new PipelineHint("The UNAS push logs were not found. Copied and scan times stay blank until " + PipelinePaths.WorkerLog + " or " + PipelinePaths.HourlyLog + " can be read.", false));
        else if (input.WorkerLogMissing)
            hints.Add(new PipelineHint("The event push log was not found at " + PipelinePaths.WorkerLog + ". The hourly log is still used.", false));
        return hints;
    }

    private static string JoinNames(IReadOnlyList<string> names)
    {
        if (names.Count == 2)
            return names[0] + " and " + names[1];
        return string.Join(", ", names.Take(names.Count - 1)) + ", and " + names[^1];
    }

    private static void HintProblem(List<PipelineHint> hints, string name, SourceLink link)
    {
        if (!link.Configured || link.Problem is null)
            return;
        var text = link.Problem switch
        {
            "refused" => name + " refused the API key. Enter it again under Settings.",
            "unreadable" => "The saved " + name + " key could not be read. Enter it again under Settings.",
            _ => name + " did not answer. Check the URL and API key under Settings."
        };
        hints.Add(new PipelineHint(text, true));
    }

    private static string KindLabel(string kind) => kind switch
    {
        "movie" => "Movie",
        "episode" => "Episode",
        "album" => "Album",
        _ => "Folder"
    };

    private static int Rank(string badge) => badge switch
    {
        "failed" => 0,
        "stuck" => 1,
        "downloaded" or "imported" or "copied" or "scan" or "pending" or "unknown" => 2,
        "in-plex" => 3,
        _ => 4
    };

    private sealed record CopyInfo(bool Reached, DateTimeOffset? At, bool Failed, DateTimeOffset? FailureUtc, string? Failure, long? Bytes, string Relative);
}
