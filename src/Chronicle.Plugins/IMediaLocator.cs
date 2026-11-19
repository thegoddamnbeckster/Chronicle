using Chronicle.Plugins.Models;

namespace Chronicle.Plugins;

/// <summary>Which kind of media a locator is asked about.</summary>
public enum LocatorFamily { Movie, Tv, Music, Book }

/// <summary>What Chronicle knows about the thing it wants identified.</summary>
/// <param name="Title">Title as the caller read it (Kodi's folder-derived title, a scanned name). Always set.</param>
/// <param name="Year">Release year when known.</param>
/// <param name="FileName">File name including its extension, when the caller has one (movies scraped by Kodi do).</param>
/// <param name="FolderName">Name of the folder holding the file, when known.</param>
public sealed record LocatorQuery(string Title, int? Year = null, string? FileName = null, string? FolderName = null);

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
}
