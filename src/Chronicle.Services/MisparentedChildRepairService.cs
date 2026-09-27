using System.Text.Json.Nodes;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: a level-2 item (an audiobook, say) whose parent is
/// ANOTHER level-2 item is moved under the level-1 item it belongs to.
///
/// Root-caused live (2026-09-27): William Hertling's "Singularity" series books (A.I. Apocalypse, The Last
/// Firewall, The Turing Exception) sat under Jeremy Robinson's BOOK "Singularity" (Infinite #13) -- a book
/// under a book, under the wrong author -- because they were first imported before Hertling's own
/// "Singularity" series item existed. A later scan finds an existing item by its file path and keeps the
/// parent it already has, so the mistake never healed. "Bear Head"/"Bee Speaker" under the book
/// "Dogs of War" and "The Emperor's Soul" under the book "Elantris" are the same shape.
///
/// The item's own folder says where it belongs: the folder above its folder is its author's folder, and
/// the author's child series with the same name as the wrong parent is the right parent. An item with no
/// such author folder / series is left alone rather than guessed.
/// </summary>
public sealed class MisparentedChildRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<MisparentedChildRepairService> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Book-level items (level 2) parented by a same-level item, in types that have a series level.
        var suspects = await (
                from m in db.MediaItems
                join p in db.MediaItems on m.ParentId equals p.Id
                where m.HierarchyLevel == 2 && p.HierarchyLevel == 2 && m.MediaType!.HierarchyLevels > 2
                select new { Child = m, Parent = p })
            .ToListAsync(ct);
        if (suspects.Count == 0)
        {
            logger.LogInformation("Misparented child repair: nothing to repair");
            return;
        }

        var typeIds = suspects.Select(s => s.Child.MediaTypeId).Distinct().ToList();
        var authors = (await db.MediaItems
                .Where(a => a.HierarchyLevel == 0 && typeIds.Contains(a.MediaTypeId))
                .Select(a => new { a.Id, a.MediaTypeId, a.MetadataJson })
                .ToListAsync(ct))
            .Select(a => (a.Id, a.MediaTypeId, Folder: NormalizeFolder(FolderOf(a.MetadataJson))))
            .Where(a => a.Folder is not null)
            .ToList();

        var moved = 0;
        foreach (var s in suspects)
        {
            var childFolder = FolderOf(s.Child.MetadataJson);
            var authorFolder = NormalizeFolder(ParentFolder(childFolder));
            if (authorFolder is null) continue;

            var author = authors.FirstOrDefault(a => a.MediaTypeId == s.Child.MediaTypeId && a.Folder == authorFolder);
            if (author == default) continue;

            var series = await db.MediaItems.FirstOrDefaultAsync(i =>
                i.ParentId == author.Id && i.HierarchyLevel == 1 && i.MediaTypeId == s.Child.MediaTypeId &&
                i.Name.ToLower() == s.Parent.Name.ToLower(), ct);
            if (series is null) continue;

            logger.LogInformation(
                "Misparented child repair: \"{Child}\" ({ChildId}) was under the same-level item \"{Parent}\" ({ParentId}); moved under series \"{Series}\" ({SeriesId})",
                s.Child.Name, s.Child.Id, s.Parent.Name, s.Parent.Id, series.Name, series.Id);
            s.Child.ParentId = series.Id;
            s.Child.UpdatedAt = DateTime.UtcNow;
            moved++;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Misparented child repair: moved {Count} item(s) under their series", moved);
    }

    /// <summary>The item's own scanned folder (fileScanner.folderPath, else its first file path).</summary>
    internal static string? FolderOf(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            if (JsonNode.Parse(metadataJson) is not JsonObject root || root["fileScanner"] is not JsonObject scanner)
                return null;
            if (scanner["folderPath"] is JsonValue fp && fp.TryGetValue<string>(out var folder) && !string.IsNullOrWhiteSpace(folder))
                return folder;
            if (scanner["filePaths"] is JsonArray { Count: > 0 } paths && paths[0] is JsonValue first &&
                first.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path))
                return path;
        }
        catch (System.Text.Json.JsonException) { }
        return null;
    }

    internal static string? ParentFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.TrimEnd('\\', '/');
        var i = trimmed.LastIndexOfAny(['\\', '/']);
        return i <= 0 ? null : trimmed[..i];
    }

    internal static string? NormalizeFolder(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
}
