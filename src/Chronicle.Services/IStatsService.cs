namespace Chronicle.Services
{
    public record UserStats(
        int TotalItemsTracked,
        int TotalCompleted,
        int TotalWatching,
        long TotalScrobbles,
        int TotalMinutesWatched,
        int ScrobblesThisWeek,
        int ScrobblesThisMonth
    );

    public interface IStatsService
    {
        Task<UserStats> GetUserStatsAsync(int userId, CancellationToken ct = default);
    }
}
