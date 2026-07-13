using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Scan;

namespace Chronicle.Services;

public interface IMetadataContributionService
{
    /// Merges an external contributor's metadata (and, optionally, a file-identity snapshot)
    /// into the item's own source partition, following the same merge → resolve → save
    /// sequence every other metadata writer uses. Does not itself decide auth/existence —
    /// callers load the tracked item first.
    Task<ContributionOutcome> ContributeAsync(
        MediaItem item,
        ChronicleDbContext db,
        string source,
        JsonElement metadataPayload,
        FileIdentitySnapshot? file,
        CancellationToken ct = default);
}

public sealed record ContributionOutcome(
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    bool FingerprintChanged,
    bool TagMismatchDetected,
    bool RematchQueued)
{
    public static ContributionOutcome Fail(string code, string message) =>
        new(false, code, message, false, false, false);
}
