namespace SyncSentry.Pipeline;

public sealed record JourneyStep(string Name, string? Detail);

public sealed record Journey(bool Upgrade, IReadOnlyList<JourneyStep> Steps, DateTimeOffset? Ended)
{
    public string Text
    {
        get
        {
            var parts = Steps.Select(step => string.IsNullOrWhiteSpace(step.Detail) ? step.Name : step.Name + " (" + step.Detail + ")");
            var text = string.Join(" → ", parts);
            if (Upgrade)
                text = "Upgrade: " + text;
            if (Ended is not null)
                text += " (" + PipelineTime.FormatDay(Ended.Value) + ")";
            return text;
        }
    }

    public bool Warning => Steps.Any(step => step.Name == "Failed");
}

public sealed record TimelineEntry(
    DateTimeOffset When,
    string Source,
    string Action,
    string? Release,
    string? Quality,
    long? Bytes,
    string? Error);

/// <summary>Turns *arr history, the push log, and Plex into one journey per download.</summary>
public static class PipelineJourneys
{
    public static (IReadOnlyList<Journey> Journeys, IReadOnlyList<TimelineEntry> Timeline) Collect(
        ArrItem? item,
        PushFolder? folder,
        PlexHit? plex,
        bool onDisk,
        string source)
    {
        var events = EventsOf(item);
        var groups = Groups(events);
        var drafts = new List<Draft>();
        var extendFile = true;
        foreach (var group in groups)
        {
            var draft = DraftOf(group, item, folder, plex, onDisk, extendFile);
            if (!draft.Failed)
                extendFile = false;
            drafts.Add(draft);
        }

        var downloadFailedOnly = drafts.Count > 0 && drafts.All(draft => draft.DownloadFailed);
        if (downloadFailedOnly && (item?.HasFile == true || plex is not null))
        {
            var arrival = Arrival(item, folder, plex, onDisk);
            if (arrival.Steps.Count > 0)
                drafts.Insert(0, arrival);
        }

        var journeys = new List<Journey>();
        var plexUsed = false;
        foreach (var draft in drafts)
        {
            if (draft.Steps.Count == 0)
                continue;
            var inPlex = draft.Steps.Any(step => step.Name == "In Plex");
            journeys.Add(new Journey(journeys.Count > 0, draft.Steps, End(draft, plex, inPlex && !plexUsed)));
            if (inPlex)
                plexUsed = true;
        }

        if (journeys.Count == 0)
        {
            var folderJourney = FolderOnly(folder, plex, onDisk);
            if (folderJourney is not null)
                journeys.Add(folderJourney);
        }

        return (journeys, Timeline(events, folder, plex, source));
    }

    private static List<ArrEvent> EventsOf(ArrItem? item)
    {
        if (item?.Events is { Count: > 0 })
            return item.Events.OrderBy(entry => entry.When).ToList();
        if (item is null)
            return [];

        var events = new List<ArrEvent>();
        if (item.GrabbedUtc is DateTimeOffset grabbed && !item.DownloadFailed)
            events.Add(new ArrEvent("grabbed", "grab", grabbed, item.DownloadId, null, null, null, null));
        else if (item.GrabbedUtc is DateTimeOffset failedAt && item.DownloadFailed && item.ImportedUtc is null)
            events.Add(new ArrEvent("downloadFailed", "fail", failedAt, item.DownloadId ?? "upgrade", null, null, null, item.Failure));
        if (item.ImportedUtc is DateTimeOffset imported)
            events.Add(new ArrEvent("import", "import", imported, item.DownloadId, null, null, null, null));
        if (item.DownloadFailed && item.ImportedUtc is not null)
        {
            var when = item.GrabbedUtc is DateTimeOffset later && later > item.ImportedUtc ? later : item.ImportedUtc.Value.AddMinutes(1);
            events.Add(new ArrEvent("downloadFailed", "fail", when, "upgrade", null, null, null, item.Failure));
        }

        return events;
    }

    private static List<List<ArrEvent>> Groups(IReadOnlyList<ArrEvent> events)
    {
        var groups = new List<List<ArrEvent>>();
        var byDownload = new Dictionary<string, List<ArrEvent>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in events)
        {
            if (!string.IsNullOrEmpty(entry.DownloadId))
            {
                if (!byDownload.TryGetValue(entry.DownloadId, out var list))
                {
                    list = [];
                    byDownload[entry.DownloadId] = list;
                    groups.Add(list);
                }

                list.Add(entry);
                continue;
            }

            var host = groups.LastOrDefault(group => group.Count > 0 && group[0].When <= entry.When);
            if (host is null)
            {
                host = [];
                groups.Add(host);
            }

            host.Add(entry);
        }

        return groups;
    }

    private static Draft DraftOf(IReadOnlyList<ArrEvent> group, ArrItem? item, PushFolder? folder, PlexHit? plex, bool onDisk, bool extendFile)
    {
        var draft = new Draft { Started = group.Min(entry => entry.When) };
        draft.GrabAt = group.Where(entry => entry.Kind is "grab" or "fail").Select(entry => (DateTimeOffset?)entry.When).FirstOrDefault();
        draft.ImportAt = group.Where(entry => entry.Kind == "import").Select(entry => (DateTimeOffset?)entry.When).LastOrDefault();
        var imported = group.Any(entry => entry.Kind == "import");
        var failed = group.Any(entry => entry.Kind == "fail") && !imported;
        if (group.Any(entry => entry.Kind is "grab" or "fail") || imported)
            draft.Steps.Add(new JourneyStep("Downloaded", null));
        if (failed)
        {
            var failure = group.Last(entry => entry.Kind == "fail");
            draft.Failed = true;
            draft.DownloadFailed = true;
            draft.FailedAt = failure.When;
            draft.Steps.Add(new JourneyStep("Failed", failure.Failure));
            return draft;
        }

        if (group.Any(entry => entry.Kind == "dismiss") && !imported)
        {
            draft.Steps.Add(new JourneyStep("Ignored", null));
            return draft;
        }

        if (imported || (extendFile && item?.HasFile == true))
        {
            draft.Steps.Add(new JourneyStep("Imported", null));
            imported = true;
        }

        if (!imported)
            return draft;
        if (CopyFailed(folder, onDisk, draft.Started))
        {
            draft.Failed = true;
            draft.FailedAt = folder?.FailureUtc ?? draft.ImportAt;
            draft.Steps.Add(new JourneyStep("Failed", folder?.Failure));
            return draft;
        }

        if (onDisk || CopyCovers(folder, draft.Started))
        {
            draft.CopyAt = folder?.CopiedUtc;
            draft.Steps.Add(new JourneyStep("Copied", null));
        }

        if (plex is not null)
            draft.Steps.Add(new JourneyStep("In Plex", null));
        return draft;
    }

    private static Draft Arrival(ArrItem? item, PushFolder? folder, PlexHit? plex, bool onDisk)
    {
        var draft = new Draft { Started = item?.ImportedUtc ?? folder?.CopiedUtc ?? plex?.AddedUtc };
        if (item?.HasFile == true || item?.ImportedUtc is not null)
            draft.Steps.Add(new JourneyStep("Imported", null));
        var copied = onDisk || (folder?.CopiedUtc is not null && folder.Failed == false);
        if (copied)
        {
            draft.CopyAt = folder?.CopiedUtc;
            draft.Steps.Add(new JourneyStep("Copied", null));
        }

        if (plex is not null)
            draft.Steps.Add(new JourneyStep("In Plex", null));
        draft.ImportAt = item?.ImportedUtc;
        return draft;
    }

    private static Journey? FolderOnly(PushFolder? folder, PlexHit? plex, bool onDisk)
    {
        var steps = new List<JourneyStep>();
        DateTimeOffset? ended = null;
        if (folder is { Failed: true } && !onDisk && folder.CopiedUtc is null)
        {
            steps.Add(new JourneyStep("Failed", folder.Failure));
            ended = folder.FailureUtc;
        }
        else if (folder?.CopiedUtc is not null || onDisk)
        {
            steps.Add(new JourneyStep("Copied", null));
            ended = folder?.CopiedUtc;
        }

        if (plex is not null)
        {
            steps.Add(new JourneyStep("In Plex", null));
            ended = plex.AddedUtc ?? ended;
        }

        return steps.Count == 0 ? null : new Journey(false, steps, ended);
    }

    private static DateTimeOffset? End(Draft draft, PlexHit? plex, bool firstPlex)
    {
        if (draft.FailedAt is not null)
            return draft.FailedAt;
        if (firstPlex && plex?.AddedUtc is DateTimeOffset added && (draft.Started is null || added >= draft.Started.Value.AddHours(-12)))
            return added;
        return draft.CopyAt ?? draft.ImportAt ?? draft.GrabAt ?? draft.Started;
    }

    private static bool CopyCovers(PushFolder? folder, DateTimeOffset? started)
    {
        if (folder?.CopiedUtc is not DateTimeOffset copied)
            return false;
        return started is null || copied >= started.Value.AddMinutes(-2);
    }

    private static bool CopyFailed(PushFolder? folder, bool onDisk, DateTimeOffset? started)
    {
        if (folder is not { Failed: true } || onDisk)
            return false;
        if (folder.FailureUtc is not DateTimeOffset failed)
            return true;
        return started is null || failed >= started.Value.AddMinutes(-2);
    }

    private static List<TimelineEntry> Timeline(IReadOnlyList<ArrEvent> events, PushFolder? folder, PlexHit? plex, string source)
    {
        var entries = new List<TimelineEntry>();
        foreach (var entry in events)
        {
            entries.Add(new TimelineEntry(
                entry.When,
                source,
                string.IsNullOrWhiteSpace(entry.EventType) ? entry.Kind : entry.EventType,
                entry.Release,
                entry.Quality,
                entry.Bytes,
                entry.Failure));
        }

        if (folder?.CopiedUtc is DateTimeOffset copied)
            entries.Add(new TimelineEntry(copied, "push log", "Copied", folder.RelativeFolder, null, folder.Bytes, null));
        if (folder is { Failed: true, FailureUtc: DateTimeOffset failedAt })
            entries.Add(new TimelineEntry(failedAt, "push log", "Copy failed", folder.RelativeFolder, null, null, folder.Failure));
        if (folder?.ScanUtc is DateTimeOffset scan)
            entries.Add(new TimelineEntry(scan, "push log", "Plex scan asked", folder.RelativeFolder, null, null, null));
        if (plex?.AddedUtc is DateTimeOffset added)
            entries.Add(new TimelineEntry(added, "Plex", "added", plex.Title, null, null, null));
        return entries.OrderBy(entry => entry.When).ToList();
    }

    private sealed class Draft
    {
        public List<JourneyStep> Steps { get; } = [];

        public bool Failed { get; set; }

        public bool DownloadFailed { get; set; }

        public DateTimeOffset? Started { get; set; }

        public DateTimeOffset? GrabAt { get; set; }

        public DateTimeOffset? ImportAt { get; set; }

        public DateTimeOffset? CopyAt { get; set; }

        public DateTimeOffset? FailedAt { get; set; }
    }
}
