using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Chronicle.Services.Notifications
{
    public sealed record NotificationDto(int Id, string Kind, string Title, string? Body, string? Link, DateTime CreatedAt, bool IsRead);
    public sealed record NotificationPage(int Unread, IReadOnlyList<NotificationDto> Items);

    public interface INotificationService
    {
        /// <summary>Tells every active administrator. Returns how many people were actually notified (muted kinds and duplicates are skipped).</summary>
        Task<int> NotifyAdminsAsync(string kind, string title, string? body = null, string? link = null,
            string? dedupeKey = null, bool once = false, CancellationToken ct = default);

        Task<int> NotifyUserAsync(int userId, string kind, string title, string? body = null, string? link = null,
            string? dedupeKey = null, bool once = false, CancellationToken ct = default);

        Task<NotificationPage> ListAsync(int userId, int limit, bool unreadOnly, CancellationToken ct = default);
        Task<int> UnreadCountAsync(int userId, CancellationToken ct = default);

        /// <summary>Only ever the caller's own: another person's id behaves exactly like a missing one.</summary>
        Task<bool> MarkReadAsync(int userId, int id, CancellationToken ct = default);
        Task<int> MarkAllReadAsync(int userId, CancellationToken ct = default);
        Task<bool> DeleteAsync(int userId, int id, CancellationToken ct = default);
        Task<int> DeleteReadAsync(int userId, CancellationToken ct = default);

        Task<int> PurgeOlderThanAsync(TimeSpan age, CancellationToken ct = default);
    }

    public sealed class NotificationService : INotificationService
    {
        public const int MaxTitle = 200, MaxBody = 1000, MaxLink = 300, MaxKey = 200, MaxPageSize = 200;
        public const string RetainDaysKey = "notifications.retain_days";
        public const int DefaultRetainDays = 90;

        private readonly ChronicleDbContext _db;
        private readonly TimeProvider _clock;
        private readonly ILogger _log = Log.ForContext<NotificationService>();

        public NotificationService(ChronicleDbContext db, TimeProvider? clock = null)
        {
            _db = db;
            _clock = clock ?? TimeProvider.System;
        }

        public async Task<int> NotifyAdminsAsync(string kind, string title, string? body = null, string? link = null,
            string? dedupeKey = null, bool once = false, CancellationToken ct = default)
        {
            var admins = await _db.Users.AsNoTracking().Where(u => u.IsAdmin && u.IsActive).Select(u => u.Id).ToListAsync(ct);
            return await DeliverAsync(admins, kind, title, body, link, dedupeKey, once, ct);
        }

        public async Task<int> NotifyUserAsync(int userId, string kind, string title, string? body = null, string? link = null,
            string? dedupeKey = null, bool once = false, CancellationToken ct = default) =>
            await DeliverAsync([userId], kind, title, body, link, dedupeKey, once, ct);

        private async Task<int> DeliverAsync(List<int> userIds, string kind, string title, string? body, string? link,
            string? dedupeKey, bool once, CancellationToken ct)
        {
            if (!NotificationKinds.IsKnown(kind)) throw new ArgumentException($"Unknown notification kind '{kind}'.", nameof(kind));
            if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A notification needs a title.", nameof(title));
            if (userIds.Count == 0) return 0;

            var muted = await MutedByAsync(userIds, kind, ct);
            var recipients = userIds.Where(id => !muted.Contains(id)).ToList();
            if (recipients.Count == 0) return 0;

            var key = dedupeKey is null ? null : Clip(dedupeKey, MaxKey);
            if (key is not null)
            {
                // "Already told them": while the earlier one is still unread, or ever (for a thing announced once).
                var existing = await _db.Notifications.AsNoTracking()
                    .Where(n => recipients.Contains(n.UserId) && n.Kind == kind && n.DedupeKey == key && (once || n.ReadAt == null))
                    .Select(n => n.UserId).Distinct().ToListAsync(ct);
                recipients = recipients.Except(existing).ToList();
                if (recipients.Count == 0) return 0;
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            foreach (var id in recipients)
                _db.Notifications.Add(new Notification
                {
                    UserId = id, Kind = kind, Title = Clip(title.Trim(), MaxTitle)!, Body = Clip(body?.Trim(), MaxBody),
                    Link = SafeLink(link), DedupeKey = key, CreatedAt = now,
                });
            await _db.SaveChangesAsync(ct);
            return recipients.Count;
        }

        /// <summary>People who switched this kind off in their preferences.</summary>
        private async Task<HashSet<int>> MutedByAsync(List<int> userIds, string kind, CancellationToken ct)
        {
            var prefs = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).Select(u => new { u.Id, u.PreferencesJson }).ToListAsync(ct);
            var muted = new HashSet<int>();
            foreach (var p in prefs)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<UserPreferences>(p.PreferencesJson);
                    if (parsed?.MutedNotificationKinds?.Contains(kind) == true) muted.Add(p.Id);
                }
                catch (JsonException ex) { _log.Debug(ex, "Unreadable preferences for user {UserId}; sending notifications anyway", p.Id); }
            }
            return muted;
        }

        public async Task<NotificationPage> ListAsync(int userId, int limit, bool unreadOnly, CancellationToken ct = default)
        {
            limit = Math.Clamp(limit, 1, MaxPageSize);
            var query = _db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
            if (unreadOnly) query = query.Where(n => n.ReadAt == null);
            var rows = await query.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Take(limit).ToListAsync(ct);
            return new NotificationPage(await UnreadCountAsync(userId, ct),
                rows.Select(n => new NotificationDto(n.Id, n.Kind, n.Title, n.Body, n.Link, n.CreatedAt, n.ReadAt != null)).ToList());
        }

        public Task<int> UnreadCountAsync(int userId, CancellationToken ct = default) =>
            _db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, ct);

        public async Task<bool> MarkReadAsync(int userId, int id, CancellationToken ct = default)
        {
            var n = await _db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
            if (n is null) return false;
            n.ReadAt ??= _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(ct);
            return true;
        }

        public async Task<int> MarkAllReadAsync(int userId, CancellationToken ct = default)
        {
            var unread = await _db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null).ToListAsync(ct);
            var now = _clock.GetUtcNow().UtcDateTime;
            foreach (var n in unread) n.ReadAt = now;
            await _db.SaveChangesAsync(ct);
            return unread.Count;
        }

        public async Task<bool> DeleteAsync(int userId, int id, CancellationToken ct = default)
        {
            var n = await _db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
            if (n is null) return false;
            _db.Notifications.Remove(n);
            await _db.SaveChangesAsync(ct);
            return true;
        }

        public async Task<int> DeleteReadAsync(int userId, CancellationToken ct = default)
        {
            var read = await _db.Notifications.Where(n => n.UserId == userId && n.ReadAt != null).ToListAsync(ct);
            _db.Notifications.RemoveRange(read);
            await _db.SaveChangesAsync(ct);
            return read.Count;
        }

        public async Task<int> PurgeOlderThanAsync(TimeSpan age, CancellationToken ct = default)
        {
            var cutoff = _clock.GetUtcNow().UtcDateTime - age;
            var old = await _db.Notifications.Where(n => n.CreatedAt < cutoff).ToListAsync(ct);
            _db.Notifications.RemoveRange(old);
            await _db.SaveChangesAsync(ct);
            return old.Count;
        }

        // ── helpers ───────────────────────────────────────────────────────────

        private static string? Clip(string? s, int max) => s is null ? null : (s.Length <= max ? s : s[..max]);

        /// <summary>Only an in-app path is allowed as a link: "/settings/x", not "//host", "https://...", or "javascript:...".</summary>
        internal static string? SafeLink(string? link)
        {
            if (string.IsNullOrWhiteSpace(link)) return null;
            var l = link.Trim();
            if (!l.StartsWith('/') || l.StartsWith("//") || l.Contains('\\') || l.Any(char.IsControl) || l.Length > MaxLink) return null;
            return l;
        }
    }
}
