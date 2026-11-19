using Chronicle.Plugins.Models;

namespace Chronicle.Plugins;

/// <summary>Which kind of media a locator is asked about.</summary>
public enum LocatorFamily { Movie, Tv, Music, Book }

/// <summary>What Chronicle knows about the thing it wants identified.</summary>
/// <param name="Title">Title as the caller read it (Kodi's folder-derived title, a scanned name). Always set.</param>
/// <param name="Year">Release year when known.</param>
/// <param name="FileName">File name including its extension, when the caller has one (movies scraped by Kodi do).</param>
/// <param name="FolderName">Name of the folder holding the file, when known.</param>
public sealed record LocatorQuery(string Title, int? Year = null, string? FileName = null, string? FolderName = null)
{
    // Init properties, not constructor parameters: adding a parameter would change the constructor signature and break every
    // plugin already compiled against the four-argument form.

    /// <summary>The item's media type by name ("music", "books"), set when Chronicle asks about a library item (see
    /// <see cref="IMediaLocator.LocateItemAsync"/>).</summary>
    public string? MediaTypeName { get; init; }

    /// <summary>The name of the item's parent: an album's artist, a book's author. Null for a top-level item.</summary>
    public string? ParentName { get; init; }

    /// <summary>0 for an artist or author, 1 for an album or book, 2 for a track or chapter.</summary>
    public int HierarchyLevel { get; init; }
}

/// <summary>
/// What a locator found. <see cref="ExternalIds"/> uses Chronicle's own stored forms: "tmdb" as "movie:123" or "tv:123",
/// "imdb" as "tt1234567", "tvdb" as the bare number, "musicbrainz" as "artist:{mbid}", and so on.
/// </summary>
public sealed record LocatedMedia(
    string Title,
    int? Year,
    IReadOnlyDictionary<string, string> ExternalIds,
    string? Path = null,
    /// <summary>Plain-words account of why this is the match, for the log ("Radarr has this file as The Beast (2026)").</summary>
    string? Reason = null);

/// <summary>
/// Optional. A plugin that knows which film, show, artist or book a file or title really is, because another application the
/// user runs (Radarr, Sonarr, Lidarr, Readarr) manages it, implements this. It is reference data only: Chronicle asks, it
/// never writes back, and a locator that finds nothing or fails simply leaves Chronicle to match by name as it always did.
/// All implementations must be stateless between calls apart from cached reads.
/// </summary>
public interface IMediaLocator
{
    string PluginId { get; }
    string Name { get; }
    string Version { get; }

    /// <summary>Returns the settings schema used to generate the configuration UI.</summary>
    PluginSettingsSchema GetSettingsSchema();

    /// <summary>Called after instantiation with the persisted settings, and again whenever they change.</summary>
    void Configure(IReadOnlyDictionary<string, string> settings);

    /// <summary>True when this locator can answer for <paramref name="family"/> (and is configured to).</summary>
    bool Handles(LocatorFamily family);

    /// <summary>Returns what the managing application knows about the query, or null when it does not know it.</summary>
    Task<LocatedMedia?> LocateAsync(LocatorFamily family, LocatorQuery query, CancellationToken ct = default);

    /// <summary>Verifies the connection to every application this locator is configured for.</summary>
    Task<bool> HealthCheckAsync(CancellationToken ct = default);

    /// <summary>
    /// The media types (by name, as plugins declare them: "music", "books") this locator can identify library items of, one at a time
    /// as Chronicle enriches them. Chronicle asks <see cref="LocateItemAsync"/> only for these. Default: none, so a locator written
    /// before this member existed (it answers Kodi's scrapers only) is never asked.
    /// </summary>
    IReadOnlyList<string> SupportedMediaTypes => [];

    /// <summary>
    /// Identifies one library item (an artist, album, author or book Chronicle holds) before Chronicle searches providers by its name.
    /// <see cref="LocatedMedia.ExternalIds"/> are in Chronicle's stored forms; an id under the same source key as a metadata provider
    /// (for example "musicbrainz") is given to that provider directly, and every id is kept on the item as a hint other providers may
    /// read. Returns null when the application does not know the item. Default: null.
    /// </summary>
    Task<LocatedMedia?> LocateItemAsync(LocatorQuery query, CancellationToken ct = default) => Task.FromResult<LocatedMedia?>(null);
}
