namespace Chronicle.Core.Helpers;

/// <summary>
/// Classifies a MediaItem's MediaType name into whether (and how) it belongs to Kodi's video
/// library. Originally also classified into the "movie" | "tvshow" | "episode" vocabulary
/// Kodi's VideoLibrary.Refresh* JSON-RPC methods use (see the now-removed Classify method) for
/// NfoPushService/NfoRebuildQueueService's benefit -- both deleted 2026-09-13 along with the
/// rest of the server-side NFO generation system, leaving just the MediaType-name classification
/// below, still used by KodiDeviceService (deciding whether an import should signal "new content
/// available") and MetadataResolutionService (title-promotion eligibility).
/// </summary>
public static class NfoKindHelper
{
    public static readonly string[] MovieLikeTypeNames = ["movies", "fanedits", "anime_movies"];
    public static readonly string[] ShowLikeTypeNames  = ["tv", "anime"];

    /// <summary>True for a MediaType name that belongs to Kodi's video library at all (movie-
    /// or show-like) -- used where a caller only needs "is this Kodi-relevant," e.g. deciding
    /// whether an import should signal KodiDeviceService's "new content available" flag.</summary>
    public static bool IsVideoLibraryType(string mediaTypeName) =>
        MovieLikeTypeNames.Contains(mediaTypeName) || ShowLikeTypeNames.Contains(mediaTypeName);
}
