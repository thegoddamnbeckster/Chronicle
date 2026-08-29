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
        // "episode" (no tv_ prefix) is what Kodi's own Player.GetItem/VideoLibrary
        // JSON-RPC literally returns as an item's "type" for a TV episode -- confirmed
        // in Chronicle_Scrobbler's media_info.py, whose media_type property passes it
        // straight through unchanged into every scrobble payload's mediaType field.
        // Missing here meant every TV episode scrobble/resume-lookup had a mediaType
        // this table didn't recognize, so it could never resolve to the seeded "tv"
        // type at all -- silently defeating the whole point of type-scoped matching
        // for the single most common TV scrobble source.
        "tv_show" or "tv_episode" or "show" or "tv" or "episode" => "tv",
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

        return await ResolveTypeIdPreferringLiteralAsync(db, mediaType, ct);
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
        var input = string.IsNullOrWhiteSpace(mediaType) ? "movies" : mediaType;

        var resolved = await ResolveTypeIdPreferringLiteralAsync(db, input, ct);
        if (resolved.HasValue) return resolved.Value;

        var fallback = await db.MediaTypes
            .Where(t => t.IsActive)
            .OrderBy(t => t.Id)
            .FirstOrDefaultAsync(ct);

        return fallback?.Id
            ?? throw new InvalidOperationException(
                "No active media types found in the database. Create at least one media type first.");
    }

    /// <summary>
    /// Resolves a MediaTypeId by name, checking the raw (lowercased) input against the DB
    /// BEFORE applying the built-in alias table above. A user-defined custom MediaType always
    /// wins over an alias — e.g. if a user creates a type literally named "episode" (a real,
    /// intentional use of Architecture Rule 1's "nothing hardcoded"), scrobbles/imports tagged
    /// "episode" must resolve to THAT type, not get silently redirected to the seeded "tv" row
    /// by the "episode" -> "tv" alias, which exists only to cover Kodi's own untyped item
    /// string when no such custom type exists.
    /// </summary>
    private static async Task<int?> ResolveTypeIdPreferringLiteralAsync(
        ChronicleDbContext db, string mediaType, CancellationToken ct)
    {
        var raw = mediaType.ToLowerInvariant();

        var literal = await db.MediaTypes
            .Where(t => t.Name == raw && t.IsActive)
            .Select(t => (int?)t.Id)
            .FirstOrDefaultAsync(ct);
        if (literal.HasValue)
            return literal;

        var aliased = NormalizeMediaTypeName(raw);
        if (aliased == raw)
            return null; // no alias applies, and the literal lookup above already came back empty

        return await db.MediaTypes
            .Where(t => t.Name == aliased && t.IsActive)
            .Select(t => (int?)t.Id)
            .FirstOrDefaultAsync(ct);
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

        // Excludes collection containers from candidates -- confirmed live (2026-08-29,
        // "Robot Jox Collection"): a sync event whose own title happens to exactly match
        // one of Chronicle's own movie-set container names (Simkl tracks "collections" as
        // their own trackable entity, distinct from individual movies) matched straight
        // onto the CONTAINER itself here, silently setting a watch status on something
        // that was never actually watched -- confirmed via zero backing interaction_events
        // and a shared bulk-insert timestamp with 634 other rows from the same sync run
        // (1272 total across every historical sync, once discovered). A container is never
        // a valid match target for a scrobble/import/sync event, which always represents a
        // real, individual watchable item -- ScraperController.SearchMovies already
        // excludes containers from ITS OWN candidate pool for the identical reason; this
        // matcher never had the same protection.
        //
        // Deliberately NOT using IMovieCollectionService.GetCollectionContainerIdsAsync's
        // full "has children OR collection:-prefixed external id" definition here: THAT
        // check is only ever run against a movie-scoped candidate pool (where "has
        // children" is unambiguous -- a real movie never naturally has any), but this
        // matcher runs across every media type, including TV shows and music artists,
        // which have season/album children as completely normal structure, not a sign of
        // being a synthetic container. Excluding "has children" here would have wrongly
        // excluded every legitimate TV show from ever being matched again -- exactly the
        // opposite of tonight's earlier "Rick and Morty" fix. Confirmed "Robot Jox
        // Collection" itself carries the "collection:487727" external id, so that signal
        // alone is enough without needing the type-ambiguous children check at all.
        return await db.MediaItems.FirstOrDefaultAsync(m =>
            m.MediaTypeId == mediaTypeId &&
            (!year.HasValue || m.Year == year) &&
            (m.Name == title      || m.Name == nameWithYear  ||
             m.Name == dashTitle  || m.Name == dashWithYear  ||
             m.Name == colonTitle || m.Name == colonWithYear) &&
            !db.MediaExternalIds.Any(e => e.MediaItemId == m.Id && e.ExternalId.StartsWith("collection:")),
            ct);
    }
}
