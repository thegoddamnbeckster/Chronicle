namespace Chronicle.Services;

public interface INfoPushService
{
    /// <summary>Writes this item's NFO straight to disk (built server-side, same document the
    /// Kodi addons' own "Recreate local NFO" fetches) and asks every Kodi instance that has
    /// already reported an internal id for it to reconsider that file, right now -- instead of
    /// waiting for a manual/scheduled rebuild pass or that device's own next library scan. Never
    /// throws: every failure mode (item not pushable, no known on-disk location yet, a device
    /// offline) is logged and swallowed, since this is always called as a side effect of some
    /// other operation (a metadata edit) that must still succeed on its own.</summary>
    Task PushAsync(int mediaItemId, int userId, CancellationToken ct = default);
}
