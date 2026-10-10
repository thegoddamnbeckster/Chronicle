using System.Globalization;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services.Security
{
    /// <summary>
    /// Read-only view of the <c>auth.*</c> rows of <c>app_settings</c> for code on the hot path
    /// (every request, every login attempt) that must never wait on the database. The snapshot
    /// is returned immediately and refreshed in the background once it is a minute old
    /// (stale-while-revalidate), so an admin's change takes effect within about a minute
    /// without a restart.
    /// </summary>
    public interface ICachedAppSettings
    {
        IReadOnlyDictionary<string, string> Snapshot { get; }
    }

    public static class CachedAppSettingsExtensions
    {
        /// <summary>A positive number from the snapshot, or the default when the row is
        /// missing, unparsable, zero, negative or absurdly large. Never disables a limit by accident.</summary>
        public static double GetPositive(this IReadOnlyDictionary<string, string> rows, string key, double defaultValue) =>
            rows.TryGetValue(key, out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            && v > 0 && !double.IsInfinity(v) && v < 1_000_000
                ? v : defaultValue;
    }

    public sealed class CachedAppSettings : ICachedAppSettings
    {
        public const string Prefix = "auth.";
        /// <summary>Other families read on hot paths: scanner sidecar lists.</summary>
        public const string ScanPrefix = "scan.";
        private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

        private readonly IServiceScopeFactory _scopes;
        private readonly TimeProvider _clock;
        private readonly ILogger _log = Log.ForContext<CachedAppSettings>();
        private volatile IReadOnlyDictionary<string, string> _snapshot = new Dictionary<string, string>();
        private long _loadedAtTicks;            // 0 = never loaded
        private int _refreshing;

        public CachedAppSettings(IServiceScopeFactory scopes, TimeProvider? clock = null)
        {
            _scopes = scopes;
            _clock = clock ?? TimeProvider.System;
        }

        public IReadOnlyDictionary<string, string> Snapshot
        {
            get
            {
                var now = _clock.GetUtcNow().UtcDateTime;
                var loadedAt = Interlocked.Read(ref _loadedAtTicks);
                var stale = loadedAt == 0 || now - new DateTime(loadedAt, DateTimeKind.Utc) >= CacheFor;
                if (stale && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
                    _ = Task.Run(() => RefreshAsync(now));
                return _snapshot;
            }
        }

        /// <summary>Reloads now and waits for it. Used at startup and by tests.</summary>
        public async Task RefreshAsync(DateTime? now = null)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                _snapshot = await db.AppSettings
                    .AsNoTracking()
                    .Where(s => s.Key.StartsWith(Prefix) || s.Key.StartsWith(ScanPrefix))
                    .ToDictionaryAsync(s => s.Key, s => s.Value);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Could not read auth settings from app_settings; keeping the previous values");
            }
            finally
            {
                Interlocked.Exchange(ref _loadedAtTicks, (now ?? _clock.GetUtcNow().UtcDateTime).Ticks);
                Interlocked.Exchange(ref _refreshing, 0);
            }
        }
    }
}
