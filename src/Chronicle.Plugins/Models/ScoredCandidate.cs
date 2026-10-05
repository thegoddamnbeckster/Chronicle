namespace Chronicle.Plugins.Models;

/// <summary>
/// A search candidate returned by <see cref="IMetadataProvider.SearchAsync"/>.
/// The plugin assigns the score; Chronicle applies the threshold.
/// </summary>
public record ScoredCandidate(
    /// <summary>Full metadata for this candidate. Must have a non-empty ExternalId.</summary>
    MediaMetadata Metadata,
    /// <summary>Confidence score 0–100, plugin-computed.</summary>
    int           Score,
    /// <summary>Human-readable explanation: which signals fired and why.</summary>
    string?       ScoreReason = null
)
{
    /// <summary>
    /// True when the candidate was found through an identifier Chronicle already holds for the item (a MusicBrainz
    /// id, an IMDb id ...) rather than by comparing names. Such a match is confirmed by the id, so Chronicle does
    /// not second-guess it with its name-similarity check: a name that differs only in punctuation or spelling
    /// ("C+C Music Factory" / "C C Music Factory") is still the same artist. A property rather than a constructor
    /// argument so plugins built against the older three-argument constructor keep loading.
    /// </summary>
    public bool IdentifierMatch { get; init; }
}
