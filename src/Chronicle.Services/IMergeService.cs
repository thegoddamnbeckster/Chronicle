using Chronicle.Core.Models;
using Chronicle.Data;

namespace Chronicle.Services;

public interface IMergeService
{
    /// <summary>
    /// Merges <paramref name="loserId"/> into <paramref name="winnerId"/>.
    /// Both items must share the same MediaTypeId and HierarchyLevel.
    /// The loser is deleted; winner absorbs all its data.
    /// </summary>
    Task MergeAsync(int winnerId, int loserId, int? mergedByUserId, CancellationToken ct = default);

    /// <summary>
    /// Reverses a previous merge. Recreates the loser as a stub, restores its
    /// external IDs and children, and queues re-enrichment.
    /// </summary>
    Task UnmergeAsync(int mergeId, CancellationToken ct = default);

    /// <summary>
    /// Checks whether <paramref name="winner"/>/<paramref name="loser"/> are structurally
    /// eligible to merge -- same hierarchy level, same parent for non-root items, and never one
    /// collection container merged with a non-container (that would transplant or destroy
    /// collection identity/membership onto an unrelated item). Returns null when eligible, or a
    /// human-readable reason when not. Shared by <see cref="MergeAsync"/> (which throws
    /// <see cref="InvalidOperationException"/> on a non-null reason) and
    /// <see cref="DuplicateCleanupService"/>'s unattended batch pass (which logs and skips the
    /// pair instead) -- the two callers differ only in how they react, not in what's allowed.
    /// </summary>
    Task<string?> CheckMergeEligibilityAsync(
        ChronicleDbContext db, MediaItem winner, MediaItem loser, CancellationToken ct = default);

    /// <summary>
    /// The actual merge body: re-points external IDs, children, library entries, interaction
    /// events, list items, credits, metadata_json, and enrichment rows from
    /// <paramref name="loser"/> onto <paramref name="winner"/>, records a
    /// <c>MediaItemMerges</c> row, and deletes the loser. Operates on already-loaded,
    /// already-validated entities against the caller's own <paramref name="db"/> -- it does NOT
    /// begin a transaction or call SaveChangesAsync, so the caller controls both (this is what
    /// lets <see cref="MergeAsync"/> wrap a single transaction around one merge, while
    /// <see cref="DuplicateCleanupService"/> can call this once per pair inside its own batch
    /// loop with a SaveChangesAsync after each). The single implementation both use --
    /// previously duplicated near-verbatim in each caller, which had already drifted (the
    /// batch path deduplicated InteractionEvents by (user, timestamp) before re-pointing;
    /// <see cref="MergeAsync"/> did not, and would have thrown on a unique-constraint collision
    /// the batch path already handled).
    /// </summary>
    Task MergeLoadedItemsAsync(
        ChronicleDbContext db, MediaItem winner, MediaItem loser, int? mergedByUserId, CancellationToken ct = default);
}
