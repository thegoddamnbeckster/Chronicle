using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// The one nightly task that repairs data Chronicle has attached to the wrong thing. Every pass is a
/// separate class with its own tests -- each found live and each self-contained -- but they run
/// together, in this order, as a single scheduled job rather than one job per pass:
///   1. Person Identity Split   -- a name-searched Wikipedia article whose birth year contradicts the
///                                 credit-backed provider data becomes its own person record.
///   1b. Year Overview Article Split -- a movie/show welded onto Wikipedia's own "YYYY in film"/
///                                 "YYYY in television" year-overview page splits it onto its own record.
///   2. Movie External ID Repair -- ids no provider returned for a movie (a remake's or original's)
///                                 are detached.
///   3. Movie File Year Repair  -- a video file whose name year contradicts a provider-confirmed movie
///                                 year is detached so the next scan imports it properly.
///   4. Implausible Year Repair -- an impossible year (65535, 0 ...) becomes no year.
///   5. Nested Movie Repair     -- a movie nested under another movie moves up into that movie's
///                                 collection (a movie with children is mistaken for a container and
///                                 can never be matched again).
///   6. Misparented Child Repair -- a book filed under another book (wrong series/author) moves under
///                                 its own author's same-named series.
///   7. Unknown Series Repair   -- a series literally named "(Unknown)" is dissolved into standalone books.
///   8. Series Fragment Repair  -- one-book series named "Series #N" fold back into the real series.
///   9. Book Title Fragment Repair -- a standalone book titled "Series #N" (no separate series on the
///                                 sync event) folds into that series.
///   9b. Series Name Fragment Repair -- a series item named "Base - N - (Unknown)" (the old parser's
///                                 mistaken series name before "(Unknown)" was recognised as a missing
///                                 year) folds into the real "Base" series.
///   10. Audiobook Series Number Repair -- a series book with no Number gets its position from its folder
///                                 name, so the series lists in reading order.
///   11. Album Name Year Repair -- an album named after its folder ("(2000) Title") gets the plain name and
///                                 the year in its Year field.
/// A failing pass is logged and never stops the ones after it.
/// </summary>
public sealed class LibraryIntegrityRepairService(
    PersonIdentitySplitService personSplit,
    YearOverviewArticleSplitService yearOverviewSplit,
    MovieExternalIdRepairService movieExternalIds,
    MovieFileYearRepairService movieFileYear,
    ImplausibleYearRepairService implausibleYear,
    NestedMovieRepairService nestedMovies,
    MisparentedChildRepairService misparentedChildren,
    UnknownSeriesRepairService unknownSeries,
    SeriesFragmentRepairService seriesFragments,
    BookTitleFragmentRepairService bookTitleFragments,
    SeriesNameFragmentRepairService seriesNameFragments,
    AudiobookSeriesNumberRepairService seriesNumbers,
    AlbumNameYearRepairService albumNameYear,
    ILogger<LibraryIntegrityRepairService> logger) : IScheduledTask
{
    public string TaskId      => "library_integrity_repair";
    public string DisplayName => "Library Integrity Repair";
    public string Description => "Fixes data attached to the wrong thing: people mixed with a same-named person, movies carrying another film's ids, and files attached to a movie of a different year.";
    public string DefaultCron => "45 2 * * *";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await RunPassAsync("person identity split", personSplit.ExecuteAsync, ct);
        await RunPassAsync("year overview article split", yearOverviewSplit.ExecuteAsync, ct);
        await RunPassAsync("movie external id repair", movieExternalIds.ExecuteAsync, ct);
        await RunPassAsync("movie file year repair", movieFileYear.ExecuteAsync, ct);
        await RunPassAsync("implausible year repair", implausibleYear.ExecuteAsync, ct);
        await RunPassAsync("nested movie repair", nestedMovies.ExecuteAsync, ct);
        await RunPassAsync("misparented child repair", misparentedChildren.ExecuteAsync, ct);
        await RunPassAsync("unknown series repair", unknownSeries.ExecuteAsync, ct);
        await RunPassAsync("series fragment repair", seriesFragments.ExecuteAsync, ct);
        await RunPassAsync("book title fragment repair", bookTitleFragments.ExecuteAsync, ct);
        await RunPassAsync("series name fragment repair", seriesNameFragments.ExecuteAsync, ct);
        await RunPassAsync("audiobook series number repair", seriesNumbers.ExecuteAsync, ct);
        await RunPassAsync("album name year repair", albumNameYear.ExecuteAsync, ct);
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
