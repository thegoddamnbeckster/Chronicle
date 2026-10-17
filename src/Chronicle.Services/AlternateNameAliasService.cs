using System.Text.Json;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Gives every author, artist or show the other names its providers already told Chronicle about (a stored
/// <c>alternateNames</c> list in a provider's section of the item's metadata) as aliases, the way enrichment now does for each new
/// match, so a folder named after a pen name finds the item the scanner already has. Adds only; an alias already there is left.
/// </summary>
public sealed class AlternateNameAliasService(
    IServiceScopeFactory scopeFactory,
    ILogger<AlternateNameAliasService> logger)
{
    private const int PageSize = 500;

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        int scanned = 0, added = 0, lastId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await db.MediaItems.Include(m => m.MediaType)
                .Where(m => m.Id > lastId && m.HierarchyLevel == 0 && m.MediaType!.HierarchyLevels >= 2 &&
                            m.MetadataJson != null && m.MetadataJson.Contains("\"alternateNames\":[\""))
                .OrderBy(m => m.Id).Take(PageSize).ToListAsync(ct);
            if (page.Count == 0) break;
            lastId = page[^1].Id;

            foreach (var item in page)
            {
                scanned++;
                foreach (var (pluginKey, names) in AlternateNamesByPlugin(item.MetadataJson))
                    await MetadataEnrichmentService.RecordAlternateNamesAsync(db, item, names, pluginKey, ct);
            }
            added += db.ChangeTracker.Entries<Chronicle.Core.Models.MediaItemAlias>().Count(e => e.State == EntityState.Added);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        logger.LogInformation("Alternate name aliases: {Scanned} item(s) with provider alternate names checked, {Added} alias(es) added", scanned, added);
    }

    internal static List<(string PluginKey, List<string> Names)> AlternateNamesByPlugin(string? metadataJson)
    {
        var result = new List<(string, List<string>)>();
        if (string.IsNullOrWhiteSpace(metadataJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            foreach (var plugin in doc.RootElement.EnumerateObject())
            {
                if (plugin.Name.StartsWith('_') || plugin.Value.ValueKind != JsonValueKind.Object) continue;
                if (!plugin.Value.TryGetProperty("alternateNames", out var alt) || alt.ValueKind != JsonValueKind.Array) continue;
                var names = alt.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                if (names.Count > 0) result.Add((plugin.Name, names));
            }
        }
        catch (JsonException) { }
        return result;
    }
}
