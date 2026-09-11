namespace Chronicle.Services;

/// <summary>
/// Tells every registered Kodi device to scan its video library when Chronicle's own file
/// scanner just found something new.
///
/// VideoLibrary.Refresh* (KodiRpcClient.RefreshAsync, used everywhere else in this codebase --
/// NfoPushService, NfoRebuildQueueService) only works on an item Kodi already has a library
/// entry for. It cannot make Kodi discover a brand-new file. Confirmed live (2026-09-11): a new
/// episode Chronicle's file scanner had already imported days earlier still didn't exist in a
/// registered device's own VideoLibrary at all -- every attempt to push a refresh for it failed
/// with "not found in this device's own VideoLibrary", and the NFO rebuild queue's own release-
/// to-another-device fallback couldn't help either, since no other device had it yet either. The
/// file only became visible in Kodi after a manual VideoLibrary.Scan. Reproduced live on two
/// more shows the same way (Reacher, Star Trek: Strange New Worlds) before this fix shipped.
///
/// This is the one path that closes that gap: called after a scan creates new movie/TV items,
/// it triggers VideoLibrary.Scan (no directory -- see KodiRpcClient.ScanAsync's own doc) on
/// every active device, throttled so a run that creates many items in quick succession doesn't
/// each trigger their own full scan.
/// </summary>
public interface IKodiLibraryScanService
{
    /// <summary>Best-effort, never throws: a device that's offline or fails just misses this
    /// trigger and catches up on its own next periodic scan (or a future call here).
    ///
    /// Takes the media type that was just imported, not the created items themselves -- a scan
    /// run only ever creates items of one type (ImportGroupsAsync is called once per
    /// ImportGroupsRequest.MediaTypeId), and this service has no per-item work to do anyway
    /// (VideoLibrary.Scan takes no item id, just "go look"). Looking the type up here instead
    /// of filtering a caller-supplied id list also sidesteps a real problem that list approach
    /// had: EF Core turns a large `.Contains()` list into an equally large SQL "IN (...)"
    /// clause, which risks exceeding SQLite's parameter limit on a big scan run (the very same
    /// investigation that found this bug had, the night before, imported over 8,000 items in
    /// one run). No-ops silently for a type that never reaches a Kodi VideoLibrary at all
    /// (music, audiobooks, people, etc.) -- see NfoKindHelper.IsVideoLibraryType.</summary>
    Task NotifyNewContentAsync(int mediaTypeId, CancellationToken ct = default);
}
