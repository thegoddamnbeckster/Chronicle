using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services
{
    public class StatsService : IStatsService
    {
        private readonly ChronicleDbContext _context;

        public StatsService(ChronicleDbContext context)
        {
            _context = context;
        }

        public async Task<UserStats> GetUserStatsAsync(int userId)
        {
            var now = DateTime.UtcNow;
            var weekStart = now.AddDays(-(int)now.DayOfWeek);
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            var libraryTask = _context.UserLibraries
                .Where(l => l.UserId == userId)
                .GroupBy(l => l.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync();

            var scrobbleStatsTask = _context.InteractionEvents
                .Where(e => e.UserId == userId)
                .GroupBy(_ => 1)
                .Select(g => new { Total = g.LongCount() })
                .FirstOrDefaultAsync();

            var minutesTask = _context.InteractionEvents
                .Include(e => e.MediaItem)
                .Where(e => e.UserId == userId && e.MarkedAsWatched)
                .SumAsync(e => (int?)e.MediaItem!.RuntimeMinutes ?? 0);

            var weekTask = _context.InteractionEvents
                .Where(e => e.UserId == userId && e.Timestamp >= weekStart)
                .CountAsync();

            var monthTask = _context.InteractionEvents
                .Where(e => e.UserId == userId && e.Timestamp >= monthStart)
                .CountAsync();

            await Task.WhenAll(libraryTask, scrobbleStatsTask, minutesTask, weekTask, monthTask);

            var library = await libraryTask;
            var scrobbleStats = await scrobbleStatsTask;

            int GetCount(LibraryStatus status) =>
                library.FirstOrDefault(g => g.Status == status)?.Count ?? 0;

            return new UserStats(
                TotalItemsTracked: library.Sum(g => g.Count),
                TotalCompleted: GetCount(LibraryStatus.Completed),
                TotalWatching: GetCount(LibraryStatus.Watching),
                TotalScrobbles: scrobbleStats?.Total ?? 0,
                TotalMinutesWatched: await minutesTask,
                ScrobblesThisWeek: await weekTask,
                ScrobblesThisMonth: await monthTask
            );
        }
    }
}
