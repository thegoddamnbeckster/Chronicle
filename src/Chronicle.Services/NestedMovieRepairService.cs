using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: a movie that has been made the PARENT of another movie
/// hands that child up to the collection the movie itself belongs to.
///
/// Root-caused live (2026-09-26): "The Animatrix" had been placed under "The Matrix" (a manual placement)
/// instead of into "The Matrix Collection". Anything with children is treated as a collection
/// container, and containers are excluded from every filename and title match -- so the real "The
/// Matrix" could never be found again, and every Kodi search for its file minted a brand-new duplicate
/// standalone item (which is why it never joined its Kodi set). "Paranormal Activity" (2007) had the
/// same shape.
///
/// Only flat types (movies, fan edits, anime movies) are touched -- a show's season legitimately has
/// children. The child keeps its level and its manual-placement marker; it simply moves up one level
/// to sit beside its former parent, in the same collection.
/// </summary>
public sealed class NestedMovieRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<NestedMovieRepairService> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // A flat-type item that is itself a collection member (has a parent) yet has children of its own.
        var parents = await db.MediaItems
            .Where(p => p.ParentId != null && p.MediaType!.HierarchyLevels == 1 &&
                        db.MediaItems.Any(c => c.ParentId == p.Id))
            .ToListAsync(ct);

        var moved = 0;
        foreach (var parent in parents)
        {
            var children = await db.MediaItems.Where(c => c.ParentId == parent.Id).ToListAsync(ct);
            foreach (var child in children)
            {
                child.ParentId = parent.ParentId;
                child.UpdatedAt = DateTime.UtcNow;
                moved++;
                logger.LogInformation(
                    "Nested movie repair: \"{Child}\" ({ChildId}) was nested under the movie \"{Parent}\" ({ParentId}); moved into its collection {CollectionId}",
                    child.Name, child.Id, parent.Name, parent.Id, parent.ParentId);
            }
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Nested movie repair: moved {Count} nested movie(s) up into their collection", moved);
    }
}
