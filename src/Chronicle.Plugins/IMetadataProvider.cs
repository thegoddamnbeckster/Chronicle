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

    /// <summary>Returns the settings schema used to generate the configuration UI.</summary>
    PluginSettingsSchema GetSettingsSchema();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Called once after instantiation with the user-supplied settings.
    /// Keys match <see cref="SettingDefinition.Key"/> values from the schema.
    /// </summary>
    void Configure(IReadOnlyDictionary<string, string> settings);

    // ── Core operations ───────────────────────────────────────────────────────

    /// <summary>Searches for media matching <paramref name="query"/>.</summary>
    /// <param name="mediaType">Hint for the provider (e.g. "movie", "tv").</param>
    Task<MediaMetadata> SearchAsync(
        string query,
        string mediaType,
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
