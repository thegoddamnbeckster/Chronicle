using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services.Matching;

/// <summary>
/// Single implementation of "resolve a plugin's free-text media type string to a
/// Chronicle MediaTypeId, then find an existing MediaItem by title+year scoped to that
/// type" — used by <see cref="ScrobbleService"/>, <see cref="Import.ImportService"/>, and
/// <see cref="SyncOrchestrationService"/>, which each previously carried their own copy of
/// this logic. Two of those three copies independently normalized "movie"/"film" to the
/// string "movie" (singular) — but the actual seeded <c>MediaTypes.Name</c> row is "movies"
/// (see ChronicleDbContext's seed data), so that lookup never matched and silently fell
/// back to "the first active type" (id 1, "tv") on every single call. Consolidating to one
/// alias table fixes that at the root instead of needing the same fix applied three times.
/// </summary>
public static class MediaItemMatcher
{
    /// <summary>
    /// Normalizes a plugin/import's free-text media type string to Chronicle's actual
    /// seeded <c>MediaTypes.Name</c> values. Anything not recognized here is passed through
    /// unchanged (lowercased) — Chronicle's media types are user-configurable (Architecture
    /// Rule 1: "Nothing hardcoded" beyond the plugin-facing contract), so a custom type a
    /// user has added must still be matchable by its own Name.
    /// </summary>
    public static string NormalizeMediaTypeName(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "tv_show" or "tv_episode" or "show" or "tv" => "tv",
        "movie" or "film"                            => "movies",
        "anime_episode"                              => "anime",
        "track" or "song"                            => "music",
        "book"                                       => "books",
        var other                                    => other,
    };

    /// <summary>
    /// Resolves a MediaTypeId strictly for use as a MATCH filter — returns null when the
    /// type can't be confidently resolved (blank input, or a name with no active row),
    /// rather than substituting an arbitrary type. Never widen this to "fall back to some
    /// other type": that substitution is exactly what let a scrobble/import with an
    /// unrecognized or omitted media type silently match — or fail to match — the wrong
    /// item's type. A null return means the caller must skip the title/year match entirely
    /// and go straight to stub creation — never fall back to an unscoped match, which would
    /// reintroduce the exact cross-type collision this exists to prevent (see
    /// FindByTitleYearAsync, which requires a real mediaTypeId for the same reason).
    /// </summary>
    public static async Task<int?> TryResolveMediaTypeIdForMatchAsync(
        ChronicleDbContext db, string? mediaType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return null;

        var name = NormalizeMediaTypeName(mediaType);
        return await db.MediaTypes
            .Where(t => t.Name == name && t.IsActive)
            .Select(t => (int?)t.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Resolves a MediaTypeId for STUB CREATION — unlike the match resolver above, this
    /// must always return a real id (a MediaItem can't be created without one), so it falls
    /// back to the first configured active type when the requested one doesn't exist, and
    /// defaults an omitted mediaType to "movies" (Chronicle's original scrobble-side
    /// default when a client sends no type at all). Only call this once a match attempt
    /// (TryResolveMediaTypeIdForMatchAsync + FindByTitleYearAsync) has already come back
    /// empty — using this fallback id as a match filter is the bug this type exists to fix.
    /// </summary>
    public static async Task<int> ResolveMediaTypeIdForStubAsync(
        ChronicleDbContext db, string? mediaType, CancellationToken ct)
    {
        var name = NormalizeMediaTypeName(string.IsNullOrWhiteSpace(mediaType) ? "movies" : mediaType);

        var type = await db.MediaTypes.FirstOrDefaultAsync(t => t.Name == name && t.IsActive, ct);
        if (type is not null) return type.Id;

        var fallback = await db.MediaTypes
            .Where(t => t.IsActive)
            .OrderBy(t => t.Id)
            .FirstOrDefaultAsync(ct);

        return fallback?.Id
            ?? throw new InvalidOperationException(
                "No active media types found in the database. Create at least one media type first.");
    }

    /// <summary>
    /// Finds an existing MediaItem by title (+ dash/colon variants, so file-scanner names
    /// like "A - B" match canonical titles like "A: B") and year, scoped to mediaTypeId.
    /// Deliberately requires a real, resolved mediaTypeId rather than accepting null and
    /// falling back to an unscoped match — an unscoped title+year match is exactly the
    /// cross-type collision bug this matcher exists to prevent (a movie silently landing on
    /// a same-named/same-year TV item or vice versa). When
    /// <see cref="TryResolveMediaTypeIdForMatchAsync"/> returns null, skip calling this
    /// entirely and go straight to stub creation instead of guessing.
    /// </summary>
    public static async Task<MediaItem?> FindByTitleYearAsync(
        ChronicleDbContext db, string title, int? year, int mediaTypeId, CancellationToken ct)
    {
        var dashTitle     = title.Replace(": ", " - ");
        var colonTitle    = title.Replace(" - ", ": ");
        var nameWithYear  = year.HasValue ? $"{title} ({year.Value})" : null;
        var dashWithYear  = year.HasValue ? $"{dashTitle} ({year.Value})" : null;
        var colonWithYear = year.HasValue ? $"{colonTitle} ({year.Value})" : null;

        return await db.MediaItems.FirstOrDefaultAsync(m =>
            m.MediaTypeId == mediaTypeId &&
            (!year.HasValue || m.Year == year) &&
            (m.Name == title      || m.Name == nameWithYear  ||
             m.Name == dashTitle  || m.Name == dashWithYear  ||
             m.Name == colonTitle || m.Name == colonWithYear), ct);
    }
}
