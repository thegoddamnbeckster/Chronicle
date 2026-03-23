using Chronicle.Core.Models;

namespace Chronicle.Services;

public record EnrichmentItemResult(
    int EnrichmentId,
    int MediaItemId,
    string Name,
    int? Year,
    string MediaType,
    int HierarchyLevel,
    string? PosterUrl,
    string? ExternalId,
    EnrichmentStatus Status,
    string? ErrorMessage,
    int RetryCount,
    int MaxRetries,
    DateTime? LastAttemptedAt,
    string? DiagnosticsJson,
    string? FileScannerMetadataJson
);

public record PagedEnrichmentItems(
    List<EnrichmentItemResult> Items,
    int Total,
    int Page,
    int PageSize
);
