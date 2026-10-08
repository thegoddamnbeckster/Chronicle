namespace Chronicle.Core.Models;

/// <summary>
/// Something a person should hear about without having to go looking: a scan found new items, a scheduled task
/// failed, a plugin has an update. One row per recipient, so "read" and "deleted" are each person's own.
/// </summary>
public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }

    /// <summary>One of <see cref="NotificationKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }

    /// <summary>An in-app path such as "/settings/background-tasks", or null. Never an external address.</summary>
    public string? Link { get; set; }

    /// <summary>Lets a producer say "this is the same thing again" (for example one plugin version) so it is not announced twice.</summary>
    public string? DedupeKey { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }

    public User? User { get; set; }
}

/// <summary>The kinds of notification, and what each is called when a person chooses which to receive.</summary>
public static class NotificationKinds
{
    public const string ScanImported = "scan.imported";
    public const string ScanReview = "scan.review";
    public const string TaskFailed = "task.failed";
    public const string PluginUpdate = "plugin.update";
    public const string DatabaseSize = "database.size";
    public const string PluginIntegrity = "plugin.integrity";

    public sealed record Info(string Kind, string Label, string Description);

    public static readonly IReadOnlyList<Info> All =
    [
        new(ScanImported, "New items from a scan", "A scheduled scan found and imported new items."),
        new(ScanReview, "Scan results to review", "A scan found files that look like a different kind of media than the folder they are in."),
        new(TaskFailed, "A background task failed", "A scheduled task (backup, scan, refresh...) stopped with an error."),
        new(PluginUpdate, "A plugin has an update", "A newer version of an installed plugin is available."),
        new(PluginIntegrity, "A plugin was blocked", "A plugin's files changed on disk without being installed or updated through Chronicle, so it was not loaded."),
        new(DatabaseSize, "The database is getting large", "The database has grown past the warning size."),
    ];

    public static bool IsKnown(string? kind) => kind is not null && All.Any(i => i.Kind == kind);
}
