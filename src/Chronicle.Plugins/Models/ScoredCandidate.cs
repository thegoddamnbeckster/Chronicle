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
);
