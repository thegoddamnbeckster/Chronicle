using Chronicle.Core.Models;

namespace Chronicle.Core.Helpers;

/// <summary>
/// A show's season rows indexed by season number, tolerant of the same number appearing on
/// more than one row.
///
/// Confirmed live (2026-10-02): "Big Brother" (US) carried two season rows for most season
/// numbers after an automatic dedup merged the UK show into it. ScraperController's episode
/// resolution built this index with a plain ToDictionary, so the second "Season 1" threw
/// "An item with the same key has already been added" and that show's episode resolution
/// failed on every Kodi request from 2026-09-14 on. One bad show must not break resolution, so
/// duplicates are folded here instead of throwing.
/// </summary>
public sealed class TvSeasonIndex
{
    /// <summary>The row new episodes for a season number go into: the oldest (lowest Id) row
    /// with that number, so the choice is stable from one call to the next.</summary>
    public IReadOnlyDictionary<int, MediaItem> ContainerByNumber { get; }

    /// <summary>Every episode number already present under ANY row with that season number,
    /// so an episode that exists only in a duplicate row is still treated as known and never
    /// created a second time.</summary>
    public IReadOnlyDictionary<int, HashSet<int>> EpisodeNumbersBySeasonNumber { get; }

    /// <summary>Season numbers held by more than one row, ascending. Empty for a healthy show.</summary>
    public IReadOnlyList<int> DuplicatedSeasonNumbers { get; }

    private TvSeasonIndex(
        IReadOnlyDictionary<int, MediaItem> containerByNumber,
        IReadOnlyDictionary<int, HashSet<int>> episodeNumbersBySeasonNumber,
        IReadOnlyList<int> duplicatedSeasonNumbers)
    {
        ContainerByNumber = containerByNumber;
        EpisodeNumbersBySeasonNumber = episodeNumbersBySeasonNumber;
        DuplicatedSeasonNumbers = duplicatedSeasonNumbers;
    }

    /// <param name="seasonRows">The show's season rows. Rows with a null Number are ignored.</param>
    /// <param name="episodeNumbersBySeasonId">Known episode numbers keyed by season row Id.</param>
    public static TvSeasonIndex Build(
        IEnumerable<MediaItem> seasonRows,
        IReadOnlyDictionary<int, HashSet<int>> episodeNumbersBySeasonId)
    {
        var groups = seasonRows
            .Where(s => s.Number.HasValue)
            .GroupBy(s => s.Number!.Value)
            .ToList();

        var containerByNumber = groups.ToDictionary(
            g => g.Key,
            g => g.OrderBy(s => s.Id).First());

        var episodeNumbersBySeasonNumber = groups.ToDictionary(
            g => g.Key,
            g => g.SelectMany(s => episodeNumbersBySeasonId.GetValueOrDefault(s.Id) ?? [])
                  .ToHashSet());

        var duplicated = groups
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(n => n)
            .ToList();

        return new TvSeasonIndex(containerByNumber, episodeNumbersBySeasonNumber, duplicated);
    }
}
