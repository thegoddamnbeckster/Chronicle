using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// The one nightly task that repairs data Chronicle has attached to the wrong thing. Every pass is a
/// separate class with its own tests -- each found live and each self-contained -- but they run
/// together, in this order, as a single scheduled job rather than one job per pass:
///   1. Person Identity Split   -- a name-searched Wikipedia article whose birth year contradicts the
///                                 credit-backed provider data becomes its own person record.
///   2. Movie External ID Repair -- ids no provider returned for a movie (a remake's or original's)
///                                 are detached.
///   3. Movie File Year Repair  -- a video file whose name year contradicts a provider-confirmed movie
///                                 year is detached so the next scan imports it properly.
///   4. Implausible Year Repair -- an impossible year (65535, 0 ...) becomes no year.
///   5. Nested Movie Repair     -- a movie nested under another movie moves up into that movie's
///                                 collection (a movie with children is mistaken for a container and
///                                 can never be matched again).
/// A failing pass is logged and never stops the ones after it.
/// </summary>
public sealed class LibraryIntegrityRepairService(
    PersonIdentitySplitService personSplit,
    MovieExternalIdRepairService movieExternalIds,
    MovieFileYearRepairService movieFileYear,
    ImplausibleYearRepairService implausibleYear,
    NestedMovieRepairService nestedMovies,
    ILogger<LibraryIntegrityRepairService> logger) : IScheduledTask
{
    public string TaskId      => "library_integrity_repair";
    public string DisplayName => "Library Integrity Repair";
    public string Description => "Fixes data attached to the wrong thing: people mixed with a same-named person, movies carrying another film's ids, and files attached to a movie of a different year.";
    public string DefaultCron => "45 2 * * *";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await RunPassAsync("person identity split", personSplit.ExecuteAsync, ct);
        await RunPassAsync("movie external id repair", movieExternalIds.ExecuteAsync, ct);
        await RunPassAsync("movie file year repair", movieFileYear.ExecuteAsync, ct);
        await RunPassAsync("implausible year repair", implausibleYear.ExecuteAsync, ct);
        await RunPassAsync("nested movie repair", nestedMovies.ExecuteAsync, ct);
    }

    private async Task RunPassAsync(string name, Func<CancellationToken, Task> pass, CancellationToken ct)
    {
        try
        {
            await pass(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Library integrity repair: the {Pass} pass failed; continuing with the next", name);
        }
    }
}
