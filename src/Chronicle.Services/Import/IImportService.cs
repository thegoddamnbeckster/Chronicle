using Chronicle.Plugins;

namespace Chronicle.Services.Import;

public record ImportResult(int Imported, int Skipped, List<string> Errors);

public interface IImportService
{
    /// <summary>Starts the device/PIN auth flow for the given import plugin.</summary>
    Task<DeviceAuthStart> StartAuthAsync(string pluginId, CancellationToken ct = default);

    /// <summary>Polls for auth completion. Returns NewSettings on success — caller persists them.</summary>
    Task<DeviceAuthPollResult> PollAuthAsync(string pluginId, string pollCode, CancellationToken ct = default);

    Task<bool> IsAuthenticatedAsync(string pluginId, CancellationToken ct = default);

    /// <summary>Imports the user's watch history from the given service into Chronicle.</summary>
    Task<ImportResult> ImportHistoryAsync(
        string pluginId, int userId, DateTimeOffset? since = null, CancellationToken ct = default);

    /// <summary>Imports the user's ratings from the given service into Chronicle.</summary>
    Task<ImportResult> ImportRatingsAsync(
        string pluginId, int userId, CancellationToken ct = default);

    /// <summary>Imports the user's watchlist from the given service into Chronicle.</summary>
    Task<ImportResult> ImportWatchlistAsync(
        string pluginId, int userId, CancellationToken ct = default);
}
