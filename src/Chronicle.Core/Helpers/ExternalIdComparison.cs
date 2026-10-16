namespace Chronicle.Core.Helpers;

/// <summary>
/// Decides whether two external ids from one provider prove that two items are different things. Ids that carry an entity kind
/// ("album:613526", "artist:588dea29...", "tv:1396", "movie:550") are only comparable when the kind agrees: a provider's
/// artist id on one record and its album id on another say nothing about whether the records are the same item, so they must
/// not block a duplicate merge. Plain ids ("tt0111161", "427962") are always comparable.
/// </summary>
public static class ExternalIdComparison
{
    /// <summary>The kind prefix of an id ("album" for "album:613526"), or null when the id has none.</summary>
    public static string? KindOf(string? externalId)
    {
        if (string.IsNullOrEmpty(externalId)) return null;
        var colon = externalId.IndexOf(':');
        return colon > 0 ? externalId[..colon] : null;
    }

    /// <summary>True when the two ids come from the same provider, describe the same kind of entity and differ: proof the items differ.</summary>
    public static bool Conflicts(string? sourceA, string? externalIdA, string? sourceB, string? externalIdB)
    {
        if (!string.Equals(sourceA, sourceB, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(externalIdA, externalIdB, StringComparison.OrdinalIgnoreCase)) return false;

        var kindA = KindOf(externalIdA);
        var kindB = KindOf(externalIdB);
        return kindA is null || kindB is null || string.Equals(kindA, kindB, StringComparison.OrdinalIgnoreCase);
    }
}
