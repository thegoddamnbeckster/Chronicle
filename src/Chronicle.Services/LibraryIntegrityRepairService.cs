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
///   1c. User Merge Restore     -- a person merge the user made that an earlier identity split took apart is
///                                 re-applied (the split now leaves a user's merge alone).
///   1d. Stub Flag Repair       -- an item that holds a file is not a provider's placeholder; its stub flag is
///                                 cleared (cleanups treat a stub as disposable).
///   1e. Misattached Series Repair -- a standalone book that was given another author's series (same name) is turned
///                                 back into a book: series ids and placeholder children removed, re-enriched.
///   1f. Ghost Item Repair      -- an item the scanner created whose file is gone and that holds nothing else (a
///                                 renamed folder's old name), and a series left empty by that, are removed.
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
///   10b. Series Slot Twin Repair -- a provider's placeholder book and the user's own book at the same
///                                 position in a series (no id or title ever matched them) become one item.
///   10c. Inverted Hierarchy Repair -- albums/seasons filed under a track or episode that shares the artist's or show's
///                                 name are moved under a real artist/show (created when the library has none).
///   10d. Wrong-Type Container Repair -- a person record whose children are all audiobooks (say) becomes an audiobook item.
///   11. Album Name Year Repair -- an album named after its folder ("(2000) Title") gets the plain name and
///                                 the year in its Year field.
///   12. Missing Source Reconciliation -- an item whose scanned file/folder is gone from disk loses the stale
///                                 File Scanner link (everything else about it is kept).
///   14. Alternate Name Aliases -- the other names a provider lists for an author, artist or show become aliases the
///                                 scanner can find a folder by.
///   13. Title Sanitization     -- a stray trailing parenthetical ("(film)") left in a plugin's stored title is
///                                 stripped and the item's resolved metadata recomputed.
/// A failing pass is logged and never stops the ones after it; the task still fails at the end (see PassFailures).
/// </summary>
public sealed class LibraryIntegrityRepairService(
    PersonIdentitySplitService personSplit,
    YearOverviewArticleSplitService yearOverviewSplit,
    UserMergeRestoreService userMergeRestore,
    StubFlagRepairService stubFlags,
    MisattachedSeriesRepairService misattachedSeries,
    GhostItemRepairService ghostItems,
    MovieExternalIdRepairService movieExternalIds,
    MovieFileYearRepairService movieFileYear,
    ImplausibleYearRepairService implausibleYear,
    NestedMovieRepairService nestedMovies,
    MisparentedChildRepairService misparentedChildren,
    InvertedHierarchyRepairService invertedHierarchy,
    WrongTypeContainerRepairService wrongTypeContainers,
    UnknownSeriesRepairService unknownSeries,
    SeriesFragmentRepairService seriesFragments,
    BookTitleFragmentRepairService bookTitleFragments,
    SeriesNameFragmentRepairService seriesNameFragments,
    AudiobookSeriesNumberRepairService seriesNumbers,
    SeriesSlotTwinRepairService seriesSlotTwins,
    AlbumNameYearRepairService albumNameYear,
    MissingSourceReconciliationService missingSources,
    TitleSanitizationService titleSanitization,
    AlternateNameAliasService alternateNameAliases,
    ILogger<LibraryIntegrityRepairService> logger) : IScheduledTask
{
    public string TaskId      => "library_integrity_repair";
    public string DisplayName => "Library Integrity Repair";
    public string Description => "Nightly data repair: fixes data attached to the wrong thing (people mixed with a same-named person, movies carrying another film's ids, misfiled books and series, bad years), clears File Scanner links to files that no longer exist, and strips stray \"(film)\"-style suffixes from stored titles.";
    public string DefaultCron => "45 2 * * *";
    public bool Exclusive => true; // a heavy database writer: conflicts with every other task (see TaskPriorityGate)

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var passes = new PassFailures(logger, "Library integrity repair");
        await passes.RunAsync("person identity split", personSplit.ExecuteAsync, ct);
        await passes.RunAsync("year overview article split", yearOverviewSplit.ExecuteAsync, ct);
        await passes.RunAsync("stub flag repair", stubFlags.ExecuteAsync, ct);
        await passes.RunAsync("misattached series repair", misattachedSeries.ExecuteAsync, ct);
        await passes.RunAsync("ghost item repair", ghostItems.ExecuteAsync, ct);
        await passes.RunAsync("user merge restore", userMergeRestore.ExecuteAsync, ct);
        await passes.RunAsync("movie external id repair", movieExternalIds.ExecuteAsync, ct);
        await passes.RunAsync("movie file year repair", movieFileYear.ExecuteAsync, ct);
        await passes.RunAsync("implausible year repair", implausibleYear.ExecuteAsync, ct);
        await passes.RunAsync("nested movie repair", nestedMovies.ExecuteAsync, ct);
        await passes.RunAsync("misparented child repair", misparentedChildren.ExecuteAsync, ct);
        await passes.RunAsync("inverted hierarchy repair", invertedHierarchy.ExecuteAsync, ct);
        await passes.RunAsync("wrong-type container repair", wrongTypeContainers.ExecuteAsync, ct);
        await passes.RunAsync("unknown series repair", unknownSeries.ExecuteAsync, ct);
        await passes.RunAsync("series fragment repair", seriesFragments.ExecuteAsync, ct);
        await passes.RunAsync("book title fragment repair", bookTitleFragments.ExecuteAsync, ct);
        await passes.RunAsync("series name fragment repair", seriesNameFragments.ExecuteAsync, ct);
        await passes.RunAsync("audiobook series number repair", seriesNumbers.ExecuteAsync, ct);
        await passes.RunAsync("series slot twin repair", seriesSlotTwins.ExecuteAsync, ct);
        await passes.RunAsync("album name year repair", albumNameYear.ExecuteAsync, ct);
        await passes.RunAsync("missing source reconciliation", missingSources.ExecuteAsync, ct);
        await passes.RunAsync("title sanitization", titleSanitization.ExecuteAsync, ct);
        await passes.RunAsync("alternate name aliases", alternateNameAliases.ExecuteAsync, ct);
        passes.ThrowIfAny();
    }
}
