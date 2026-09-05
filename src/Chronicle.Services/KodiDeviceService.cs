using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services;

public sealed class KodiDeviceService(ChronicleDbContext db) : IKodiDeviceService
{
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

    public async Task<List<(KodiDevice Device, KodiLibraryId Mapping)>> GetPushTargetsAsync(
        int mediaItemId, CancellationToken ct = default)
    {
        // Single joined query (was two round trips + a manual in-memory join) that also excludes
        // any device whose backing ApiToken has since been revoked: RevokeTokenAsync only flips
        // ApiToken.IsActive (it never deletes the row, so KodiDevice's cascade-on-delete FK never
        // fires), so without this filter a revoked device's last-known host:port would otherwise
        // keep receiving pushes indefinitely -- including, after a DHCP lease change, to whatever
        // device now holds that LAN IP.
        var rows = await (
            from mapping in db.KodiLibraryIds
            where mapping.MediaItemId == mediaItemId
            join device in db.KodiDevices on mapping.KodiDeviceId equals device.Id
            join token in db.ApiTokens on device.ApiTokenId equals token.Id
            where token.IsActive
            select new { device, mapping }
        ).ToListAsync(ct);

        return rows.Select(r => (r.device, r.mapping)).ToList();
    }
}
