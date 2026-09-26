namespace Chronicle.Core.Models;

/// <summary>
/// One title a person is credited on according to a metadata provider's own filmography (e.g. TMDB
/// combined_credits), including titles that are NOT in the library. Stored (rather than fetched and
/// discarded) so the person page's "show every credit" view is served from Chronicle after the first
/// fetch and refreshed only when stale -- see PersonFullCreditsService. A person's rows for a source are
/// replaced wholesale on refresh.
/// </summary>
public class PersonProviderCredit
{
    public int Id { get; set; }
    public int PersonMediaItemId { get; set; }

    /// <summary>Short-form provider source (e.g. "tmdb") -- same convention as MediaExternalId.Source.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The provider's id for the title in the form the provider uses on titles (e.g. "movie:603").</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>"movie" or "tv".</summary>
    public string MediaType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? PosterUrl { get; set; }

    /// <summary>"Actor" for acting, else the crew job.</summary>
    public string Role { get; set; } = string.Empty;
    public string? CharacterName { get; set; }

    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;

    public MediaItem PersonMediaItem { get; set; } = null!;
}
