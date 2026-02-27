using Chronicle.Core.Models;

namespace Chronicle.Services
{
    public record ScrobbleRequest(
        int MediaItemId,
        double? ProgressPercent,
        DateTime? Timestamp,
        string? DeviceName
    );

    public record ScrobbleResult(
        InteractionEvent Event,
        bool MarkedAsWatched
    );

    public interface IScrobbleService
    {
        Task<ScrobbleResult> ScrobbleAsync(int userId, ScrobbleRequest request);
        Task<IEnumerable<InteractionEvent>> GetHistoryAsync(int userId, int page = 1, int perPage = 20);
    }
}
