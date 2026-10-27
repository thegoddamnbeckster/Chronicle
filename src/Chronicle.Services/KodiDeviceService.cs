using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services;

public sealed class KodiDeviceService(ChronicleDbContext db) : IKodiDeviceService
{
    // Single global key -- see SignalNewContentAsync's own doc for why this isn't per-device or
    // per-media-type.
    private const string NewContentSignalKey = "kodi.new_content_signaled_at";

    // See ReportScanActivityAsync/IsScanActiveAsync's own docs. Stores the deadline (now + TTL)
    // directly, not just the last-heartbeat time, so IsScanActiveAsync is a single string
    // comparison with no TimeSpan arithmetic of its own.
    private const string ScanActiveUntilKey = "kodi.scan_active_until";
    private static TimeSpan ScanActivityTtl => Tunables.Minutes(Tunables.DeviceScanActiveMinutes);
    /// <summary>"Vision" + "Chronicle Scraper (Movies)" -> "Vision - Chronicle Scraper (Movies)", so a Kodi that
    /// has several add-ons shows one clearly named row per add-on. A bare device name stays as it is.</summary>
    internal static string DisplayName(string name, KodiAddonInfo? addon) =>
        string.IsNullOrWhiteSpace(addon?.AddonName) ? name : $"{name} \u2014 {addon!.AddonName!.Trim()}";

    private static void ApplyAddon(KodiDevice device, KodiAddonInfo? addon)
    {
        if (addon is null) return;
        device.AddonId = Clean(addon.AddonId);
        device.AddonName = Clean(addon.AddonName);
        device.AddonVersion = Clean(addon.AddonVersion);
        device.KodiVersion = Clean(addon.KodiVersion);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : (value.Trim().Length > 200 ? value.Trim()[..200] : value.Trim());

    public async Task RegisterAsync(int userId, int apiTokenId, string name, string host, int port,
        string? username, string? password, KodiAddonInfo? addon = null, CancellationToken ct = default)
    {
        var device = await db.KodiDevices.FirstOrDefaultAsync(d => d.ApiTokenId == apiTokenId, ct);
        var now = DateTime.UtcNow;
        var isNew = device is null;
        if (device is null)
        {
            device = new KodiDevice { ApiTokenId = apiTokenId, UserId = userId, CreatedAt = now };
            db.KodiDevices.Add(device);
        }

        device.UserId     = userId;
        device.Name       = DisplayName(name, addon);
        device.Host       = host;
        device.Port       = port;
        device.Username   = username;
        device.Password   = password;
        device.LastSeenAt = now;
        ApplyAddon(device, addon);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (isNew)
        {
            // Lost a race with a concurrent registration for the same ApiTokenId (e.g. service.py's
            // periodic heartbeat overlapping default.py's own post-pairing registration) -- the
            // unique index on api_token_id rejected our insert because another request's row landed
            // first. Detach our failed insert and update that row instead; every field here is
            // idempotent (a device re-describing itself), so there's nothing to reconcile beyond
            // "whichever write lands last wins."
            db.Entry(device).State = EntityState.Detached;
            var existing = await db.KodiDevices.FirstAsync(d => d.ApiTokenId == apiTokenId, ct);
            existing.UserId     = userId;
            existing.Name       = DisplayName(name, addon);
            existing.Host       = host;
            existing.Port       = port;
            existing.Username   = username;
            existing.Password   = password;
            existing.LastSeenAt = now;
            ApplyAddon(existing, addon);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task RecordKodiIdAsync(int apiTokenId, int mediaItemId, string kind, int kodiId, CancellationToken ct = default)
    {
        var device = await db.KodiDevices.FirstOrDefaultAsync(d => d.ApiTokenId == apiTokenId, ct);
        if (device is null) return; // remote control off on this instance -- nothing to map to

        var mapping = await db.KodiLibraryIds
            .FirstOrDefaultAsync(m => m.KodiDeviceId == device.Id && m.MediaItemId == mediaItemId, ct);
        var isNew = mapping is null;
        if (mapping is null)
        {
            mapping = new KodiLibraryId { KodiDeviceId = device.Id, MediaItemId = mediaItemId };
            db.KodiLibraryIds.Add(mapping);
        }

        mapping.Kind      = kind;
        mapping.KodiId    = kodiId;
        mapping.UpdatedAt = DateTime.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (isNew)
        {
            // Same race as RegisterAsync above, on the (KodiDeviceId, MediaItemId) unique index
            // instead -- e.g. two ordinary scans for the same item overlapping.
            db.Entry(mapping).State = EntityState.Detached;
            var existing = await db.KodiLibraryIds
                .FirstAsync(m => m.KodiDeviceId == device.Id && m.MediaItemId == mediaItemId, ct);
            existing.Kind      = kind;
            existing.KodiId    = kodiId;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task SignalNewContentAsync(string mediaTypeName, CancellationToken ct = default)
    {
        if (!MediaTypeRegistry.IsVideoLibraryType(mediaTypeName)) return;

        var setting = await db.AppSettings.FindAsync([NewContentSignalKey], ct);
        var now = DateTime.UtcNow.ToString("O");
        if (setting is null)
            db.AppSettings.Add(new AppSetting { Key = NewContentSignalKey, Value = now });
        else
            setting.Value = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsScanNeededAsync(int apiTokenId, CancellationToken ct = default)
    {
        var setting = await db.AppSettings.FindAsync([NewContentSignalKey], ct);
        if (setting is null || !DateTime.TryParse(setting.Value, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var signaledAt))
            return false;

        // No row at all (never acknowledged, ever -- including a caller that has never
        // self-registered a KodiDevice, since this is deliberately independent of that) counts
        // the same as a null LastAckAt: due.
        var ack = await db.KodiScanAcks.FirstOrDefaultAsync(a => a.ApiTokenId == apiTokenId, ct);
        return ack is null || ack.LastAckAt is null || ack.LastAckAt < signaledAt;
    }

    public async Task AcknowledgeScanAsync(int apiTokenId, CancellationToken ct = default)
    {
        var ack = await db.KodiScanAcks.FirstOrDefaultAsync(a => a.ApiTokenId == apiTokenId, ct);
        var isNew = ack is null;
        if (ack is null)
        {
            ack = new KodiScanAck { ApiTokenId = apiTokenId };
            db.KodiScanAcks.Add(ack);
        }
        ack.LastAckAt = DateTime.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (isNew)
        {
            // Same "lost the insert race" shape as RegisterAsync's own catch above -- two
            // overlapping acknowledgements for a caller with no row yet (unlikely at a 3-minute
            // poll interval, but the same insurance costs nothing).
            db.Entry(ack).State = EntityState.Detached;
            var existing = await db.KodiScanAcks.FirstAsync(a => a.ApiTokenId == apiTokenId, ct);
            existing.LastAckAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task ReportScanActivityAsync(CancellationToken ct = default)
    {
        var until = DateTime.UtcNow.Add(ScanActivityTtl).ToString("O");
        var setting = await db.AppSettings.FindAsync([ScanActiveUntilKey], ct);
        if (setting is null)
            db.AppSettings.Add(new AppSetting { Key = ScanActiveUntilKey, Value = until });
        else
            setting.Value = until;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsScanActiveAsync(CancellationToken ct = default)
    {
        var setting = await db.AppSettings.FindAsync([ScanActiveUntilKey], ct);
        if (setting is null || !DateTime.TryParse(setting.Value, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var until))
            return false;
        return DateTime.UtcNow < until;
    }

    public async Task<bool> TouchAsync(int userId, int apiTokenId, string? host, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var device = await db.KodiDevices.FirstOrDefaultAsync(d => d.ApiTokenId == apiTokenId, ct);
        var isNew = device is null;
        if (device is null)
        {
            var keyName = await db.ApiTokens.Where(t => t.Id == apiTokenId).Select(t => t.Name).FirstOrDefaultAsync(ct);
            device = new KodiDevice
            {
                ApiTokenId = apiTokenId, UserId = userId, CreatedAt = now,
                Name = string.IsNullOrWhiteSpace(keyName) ? "Kodi" : keyName,
            };
            db.KodiDevices.Add(device);
        }

        device.LastSeenAt = now;
        if (!string.IsNullOrWhiteSpace(host))
            device.Host = host;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (isNew)
        {
            // Lost the insert race with the same device's other request; stamp the row that won.
            db.Entry(device).State = EntityState.Detached;
            var existing = await db.KodiDevices.FirstAsync(d => d.ApiTokenId == apiTokenId, ct);
            existing.LastSeenAt = now;
            if (!string.IsNullOrWhiteSpace(host))
                existing.Host = host;
            await db.SaveChangesAsync(ct);
            return false;
        }
        return isNew;
    }

    // Bounds one poll's worth of refresh-due items -- see GetItemsNeedingRefreshAsync's own doc
    // for why a large backlog is drained gradually rather than sent all at once.
    private const int MaxRefreshItemsPerPoll = 50;

    public async Task<IReadOnlyList<RefreshDueItem>> GetItemsNeedingRefreshAsync(
        int apiTokenId, IReadOnlyList<string> kinds, CancellationToken ct = default)
    {
        if (kinds.Count == 0) return [];

        var device = await db.KodiDevices.FirstOrDefaultAsync(d => d.ApiTokenId == apiTokenId, ct);
        if (device is null) return []; // remote control off on this instance -- nothing tracked to compare

        return await db.KodiLibraryIds
            .Where(m => m.KodiDeviceId == device.Id && kinds.Contains(m.Kind))
            .Join(db.MediaItems, m => m.MediaItemId, i => i.Id, (m, i) => new { m.KodiId, m.Kind, m.UpdatedAt, ItemUpdatedAt = i.UpdatedAt })
            .Where(x => x.ItemUpdatedAt > x.UpdatedAt)
            .OrderBy(x => x.ItemUpdatedAt)
            .Take(MaxRefreshItemsPerPoll)
            .Select(x => new RefreshDueItem(x.KodiId, x.Kind))
            .ToListAsync(ct);
    }
}
