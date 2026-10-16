using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: a PERSON record whose children are all of one other media type (five audiobook series
/// hanging under "Iain M. Banks", a person) becomes an item of that type.
///
/// Root-caused live (2026-10-14): undoing the merge of the audiobook author "Iain M. Banks" into his person record recreated the author
/// as a person, because the merge log had stored the author's media type AFTER it was overwritten with the winner's. Merging no longer
/// does that (and refuses to hang children under an item of another type); this heals the stubs already recreated that way.
///
/// Deliberately narrow: only a person-type item (a person can never parent anything), only when every child is of the same one type
/// that has a hierarchy, and only when the person has no credits of its own (a real person with a filmography stays a person).
/// </summary>
public sealed class WrongTypeContainerRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<WrongTypeContainerRepairService> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var peopleTypeIds = await db.MediaTypes.Where(t => t.Name == "people").Select(t => t.Id).ToListAsync(ct);
        if (peopleTypeIds.Count == 0) return;

        var deepTypeIds = await db.MediaTypes.Where(t => t.HierarchyLevels >= 2).Select(t => t.Id).ToListAsync(ct);

        // People that have at least one child of a different type.
        var candidateIds = await db.MediaItems
            .Where(p => peopleTypeIds.Contains(p.MediaTypeId) && p.HierarchyLevel == 0
                        && db.MediaItems.Any(c => c.ParentId == p.Id && c.MediaTypeId != p.MediaTypeId))
            .Select(p => p.Id)
            .ToListAsync(ct);

        var retyped = 0;
        foreach (var id in candidateIds)
        {
            var childTypes = await db.MediaItems.Where(c => c.ParentId == id).Select(c => c.MediaTypeId).Distinct().ToListAsync(ct);
            if (childTypes.Count != 1 || !deepTypeIds.Contains(childTypes[0])) continue;
            if (await db.MediaCredits.AnyAsync(c => c.PersonMediaItemId == id, ct)) continue;

            var person = await db.MediaItems.FirstAsync(m => m.Id == id, ct);
            logger.LogInformation(
                "Wrong-type container repair: \"{Name}\" ({Id}) is a person but all of its children are media type {Type}; retyped",
                person.Name, person.Id, childTypes[0]);
            person.MediaTypeId = childTypes[0];
            person.UpdatedAt = DateTime.UtcNow;
            retyped++;
        }

        if (retyped > 0) await db.SaveChangesAsync(ct);
        logger.LogInformation("Wrong-type container repair: retyped {Count} item(s)", retyped);
    }
}
