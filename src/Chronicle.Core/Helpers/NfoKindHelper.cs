namespace Chronicle.Core.Helpers;

/// <summary>
/// Classifies a MediaItem into the "movie" | "tvshow" | "episode" vocabulary Kodi's
/// VideoLibrary.Refresh* JSON-RPC methods (and KodiLibraryId.Kind) use -- shared by
/// NfoPushService (per-edit/rating/watch live push) and NfoRebuildQueueService (the
/// cross-device rebuild queue) so the two never quietly disagree on what's pushable.
/// </summary>
public static class NfoKindHelper
{
    /// <summary>Public (not just used internally by Classify below) so callers that need to
    /// filter a MediaType query by name -- e.g. NfoRebuildQueueService.EnsureSeededAsync,
    /// which can't express Classify's per-item HierarchyLevel branching directly in a SQL
    /// WHERE clause -- reference the exact same list instead of redeclaring their own copy.</summary>
    public static readonly string[] MovieLikeTypeNames = ["movies", "fanedits", "anime_movies"];
    public static readonly string[] ShowLikeTypeNames  = ["tv", "anime"];

    /// <summary>Returns "movie" | "tvshow" | "episode", or null for anything else (person,
    /// music, season container, collection, etc. -- none of these are individually pushable).
    /// No HierarchyLevel gate for movies: a standalone movie sits at level 0, but a movie that
    /// belongs to a collection sits at level 1 (the collection container itself is level 0) --
    /// see NfoPushService's own original comment on this for the F9/Fast & Furious case that
    /// found it.</summary>
    public static string? Classify(string mediaTypeName, int hierarchyLevel)
    {
        if (MovieLikeTypeNames.Contains(mediaTypeName)) return "movie";
        if (ShowLikeTypeNames.Contains(mediaTypeName) && hierarchyLevel == 0) return "tvshow";
        if (ShowLikeTypeNames.Contains(mediaTypeName) && hierarchyLevel == 2) return "episode";
        return null;
    }
}
