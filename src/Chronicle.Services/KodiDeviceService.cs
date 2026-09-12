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
    private static readonly TimeSpan ScanActivityTtl = TimeSpan.FromMinutes(3);
    public async Task RegisterAsync(int userId, int apiTokenId, string name, string host, int port,
        string? username, string? password, CancellationToken ct = default)
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
        device.Name       = name;
        device.Host       = host;
        device.Port       = port;
        device.Username   = username;
        device.Password   = password;
        device.LastSeenAt = now;

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
            existing.Name       = name;
            existing.Host       = host;
            existing.Port       = port;
            existing.Username   = username;
            existing.Password   = password;
            existing.LastSeenAt = now;
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

    public async Task<int?> GetDeviceIdForApiTokenAsync(int apiTokenId, CancellationToken ct = default)
    {
        var device = await db.KodiDevices.FirstOrDefaultAsync(d => d.ApiTokenId == apiTokenId, ct);
        return device?.Id;
    }

    public async Task SignalNewContentAsync(string mediaTypeName, CancellationToken ct = default)
    {
        if (!NfoKindHelper.IsVideoLibraryType(mediaTypeName)) return;

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
}
