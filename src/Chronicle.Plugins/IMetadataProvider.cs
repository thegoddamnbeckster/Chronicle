using Chronicle.Plugins.Models;

namespace Chronicle.Plugins;

/// <summary>
/// Implemented by metadata scraper plugins (TMDB, MusicBrainz, etc.).
/// All implementations must be stateless between calls — configuration is
/// supplied once via <see cref="Configure"/> and then used for every request.
/// </summary>
public interface IMetadataProvider
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Unique reverse-domain plugin identifier. e.g. "chronicle.plugin.tmdb".</summary>
    string PluginId { get; }

    string Name { get; }
    string Version { get; }
    string Author { get; }

    // ── Capability declarations ───────────────────────────────────────────────

    /// <summary>Returns all media types this provider can supply metadata for.</summary>
    MediaTypeSupport[] GetSupportedMediaTypes();

    /// <summary>
    /// Returns the external ID prefixes this provider's <see cref="GetByIdAsync"/> can accept
    /// beyond its own native format — for example, a plugin whose native IDs are "simkl:N" may
    /// also accept "tv:N" (TMDB) and "imdb:ttN" so that cross-reference IDs from Trakt can be
    /// used to seed its enrichment row directly instead of falling back to a text search.
    ///
    /// Return an empty list (the default) if the provider only accepts its own ID format.
    /// </summary>
    IReadOnlyList<string> GetAcceptedCrossRefPrefixes() => [];

    /// <summary>Returns the settings schema used to generate the configuration UI.</summary>
    PluginSettingsSchema GetSettingsSchema();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Called once after instantiation with the user-supplied settings.
    /// Keys match <see cref="SettingDefinition.Key"/> values from the schema.
    /// </summary>
    void Configure(IReadOnlyDictionary<string, string> settings);

    // ── Core operations ───────────────────────────────────────────────────────

    /// <summary>
    /// Searches for media matching <paramref name="context"/> and returns scored candidates.
    /// The plugin is responsible for query construction, candidate retrieval, and scoring (0–100).
    /// Chronicle applies the confidence threshold to decide accept/reject.
    /// </summary>
    Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context,
        CancellationToken ct = default);

    /// <summary>Fetches full metadata for the item identified by the provider's external id.</summary>
    Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default);

    /// <summary>Downloads an image from the given URL and returns the raw bytes.</summary>
    Task<byte[]> GetImageAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Verifies that the provider can reach its upstream service with the supplied credentials.
    /// </summary>
    Task<bool> HealthCheckAsync(CancellationToken ct = default);
}
