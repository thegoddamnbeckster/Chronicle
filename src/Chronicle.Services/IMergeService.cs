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
}
