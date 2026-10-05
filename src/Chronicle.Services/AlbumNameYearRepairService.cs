using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: an album the file scanner named after its folder
/// ("(2000) The Better Life", "The Better Life (2000)") gets the plain name and the year in its Year field.
/// The items are renamed in place, so their ids, play history, artwork and enrichment are untouched. An album
/// that already has a different year keeps it; the name is still cleaned.
/// </summary>
public sealed class AlbumNameYearRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<AlbumNameYearRepairService> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Which types have an album level comes from each type's own level labels ("Artist,Album,Track"),
        // not from a hardcoded type name.
        var albumTypeIds = (await db.MediaTypes.AsNoTracking()
                .Select(t => new { t.Id, t.HierarchyLabels }).ToListAsync(ct))
            .Where(t => (t.HierarchyLabels ?? "").Split(',').ElementAtOrDefault(1)?.Trim()
                .Equals("Album", StringComparison.OrdinalIgnoreCase) == true)
            .Select(t => t.Id).ToList();
        if (albumTypeIds.Count == 0) return;

        var albums = await db.MediaItems
            .Where(m => albumTypeIds.Contains(m.MediaTypeId) && m.HierarchyLevel == 1
                        && (m.Name.Contains("(") || m.Name.Contains("[")))
            .ToListAsync(ct);

        var changed = 0;
        foreach (var album in albums)
        {
            var (name, year) = FolderNameYear.Split(album.Name);
            if (name == album.Name) continue;
            album.Name = name;
            album.Year ??= year;
            album.UpdatedAt = DateTime.UtcNow;
            changed++;
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Album name year repair: moved the year out of the name on {Count} album(s)", changed);
    }
}
