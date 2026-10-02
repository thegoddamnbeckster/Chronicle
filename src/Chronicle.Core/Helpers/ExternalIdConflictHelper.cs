using Chronicle.Core.Models;

namespace Chronicle.Core.Helpers;

/// <summary>
/// Decides whether two items' external ids prove they are DIFFERENT real-world things.
///
/// Confirmed live (2026-10-02): "Big Brother" (US, TMDB 10160) and "Big Brother" (UK, TMDB
/// 11366) share a title and a year, so the automatic duplicate passes folded the UK show into
/// the US one -- and that merge buried 21 seasons of UK episodes under the US show. Two ids
/// from the same source that disagree are direct evidence the items are distinct, whatever
/// their names share.
/// </summary>
public static class ExternalIdConflictHelper
{
    /// <summary>
    /// True when both sides carry an id from the same source and none of the values match.
    /// Ignores bookkeeping sentinels (not identity) and fanarttv, whose artist-level ids are
    /// deliberately shared by many distinct items -- the same exclusions the duplicate
    /// passes' own external-id matching already makes.
    /// </summary>
    public static bool HasConflict(IEnumerable<MediaExternalId> a, IEnumerable<MediaExternalId> b)
    {
        var aBySource = Identity(a).ToLookup(e => e.Source, e => e.ExternalId, StringComparer.OrdinalIgnoreCase);
        var bBySource = Identity(b).ToLookup(e => e.Source, e => e.ExternalId, StringComparer.OrdinalIgnoreCase);

        foreach (var source in aBySource.Select(g => g.Key))
        {
            if (!bBySource.Contains(source)) continue;
            var aIds = aBySource[source].ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!bBySource[source].Any(aIds.Contains)) return true;
        }
        return false;
    }

    private static IEnumerable<MediaExternalId> Identity(IEnumerable<MediaExternalId> ids) =>
        ids.Where(e =>
            !string.Equals(e.Source, "fanarttv", StringComparison.OrdinalIgnoreCase) &&
            !e.ExternalId.EndsWith("__suppress__", StringComparison.OrdinalIgnoreCase) &&
            !e.ExternalId.EndsWith("manual-collection-member", StringComparison.OrdinalIgnoreCase));
}
