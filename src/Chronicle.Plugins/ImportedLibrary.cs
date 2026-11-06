namespace Chronicle.Plugins;

/// <summary>
/// One title the user HAS on a platform (owned, wishlisted, installed, subscribed to), as opposed to
/// <see cref="ImportedWatchEvent"/>, which is one play at one time. A library is mostly games the user never
/// started, which a watch event cannot say without inventing a play.
/// </summary>
public record ImportedLibraryEntry(
    /// <summary>Source-namespaced id, e.g. "steam:1245620". Ownership is keyed on (user, source, this id).</summary>
    string ExternalId,
    /// <summary>Other ids the platform knows for the same title, keyed by source name.</summary>
    IReadOnlyDictionary<string, string> AdditionalIds,
    /// <summary>Chronicle media type name, e.g. "game".</summary>
    string MediaType,
    string Title,
    /// <summary>Release year when the platform's response carries one; null otherwise (never guessed).</summary>
    int? Year,
    /// <summary>Where it is held: "PC", "PS5", "Android" ...</summary>
    string Platform,
    /// <summary>The platform key, e.g. "steam". Names the ownership row's source.</summary>
    string StoreSource,
    /// <summary>Minutes played; null = the platform does not say, 0 = owned and never started.</summary>
    int? PlaytimeMinutes,
    /// <summary>Last time played; null = never, or unknown. Never a sentinel date.</summary>
    DateTimeOffset? LastPlayedAt,
    DateTimeOffset? AcquiredAt,
    /// <summary>Access through a subscription (Game Pass, PS Plus) rather than ownership.</summary>
    bool OwnedViaSubscription,
    /// <summary>True for a wishlist entry: wanted, not held.</summary>
    bool IsWishlist,
    /// <summary>The platform's complete record for this title, kept as given. Nothing is dropped.</summary>
    string RawJson,
    /// <summary>Free tags, e.g. an app type ("dlc", "demo", "music") or "unclassified".</summary>
    IReadOnlyList<string>? Tags = null,
    string? PosterUrl = null,
    string? BackdropUrl = null,
    string? Overview = null,
    /// <summary>Other names people might type for this title (the name without trademark marks, a numeral written the other way). Become aliases the search matches. Optional.</summary>
    IReadOnlyList<string>? AlternateNames = null)
{
    /// <summary>The shape before <c>AlternateNames</c> existed, kept so a plugin built against it still loads and runs unchanged.</summary>
    public ImportedLibraryEntry(
        string ExternalId, IReadOnlyDictionary<string, string> AdditionalIds, string MediaType, string Title, int? Year, string Platform,
        string StoreSource, int? PlaytimeMinutes, DateTimeOffset? LastPlayedAt, DateTimeOffset? AcquiredAt, bool OwnedViaSubscription,
        bool IsWishlist, string RawJson, IReadOnlyList<string>? Tags, string? PosterUrl, string? BackdropUrl, string? Overview)
        : this(ExternalId, AdditionalIds, MediaType, Title, Year, Platform, StoreSource, PlaytimeMinutes, LastPlayedAt, AcquiredAt,
               OwnedViaSubscription, IsWishlist, RawJson, Tags, PosterUrl, BackdropUrl, Overview, null) { }
}

/// <summary>Account-level facts a platform reports about the user (level, badges, display name). Kept whole.</summary>
public record ImportedAccountInfo(string DisplayName, string RawJson);
