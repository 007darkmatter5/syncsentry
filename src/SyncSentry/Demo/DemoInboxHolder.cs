namespace SyncSentry.Demo;

/// <summary>Lets the settings page know whether the in-process ntfy inbox is active.</summary>
public sealed class DemoInboxHolder(bool enabled)
{
    public bool Enabled { get; } = enabled;
}
