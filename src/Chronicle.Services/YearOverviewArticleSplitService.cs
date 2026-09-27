using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Scheduled task, the movie/TV counterpart of <see cref="PersonIdentitySplitService"/>: splits a title
/// record that has been welded onto Wikipedia's own "YYYY in film"/"YYYY in television" year-overview
/// article -- a real Wikipedia page, but about the whole year, never about one title -- while another
/// provider (TMDB, FanartTV, SIMKL) correctly identifies it as one specific real movie or show.
///
/// Root-caused live (2026-09-27): ten movie items were each a real film (Cliffhanger, Alexander, Rango,
/// Whiplash, Going in Style, Yesterday, Seance, Hounded, Presence, Back in Action) fully identified by
/// TMDB/FanartTV/SIMKL, yet each ALSO carried the Wikipedia article for "the year's films" and had been
/// renamed to that article's title ("2022 in film" for what TMDB knows as "Hounded") -- MetadataResolutionService
/// promotes a resolved title onto item.Name, and Wikipedia apparently won that resolution. One of the ten
/// ("2022 in film") was already blocking a second, correctly-named "Hounded" item the file scanner created:
/// TMDB/FanartTV/SIMKL enrichment for the real item found their ids already claimed by the wrong one and
/// (correctly) refused to steal them, leaving the real item with no provider data at all.
///
/// A structural signal, not a guess: Wikipedia's own title convention for these year-overview pages is
/// exactly "YYYY in film"/"YYYY in television" (see <see cref="YearOverviewTitle"/>) -- it can never be a
/// single title's real name. The provable condition mirrors the person split: the record must ALSO carry
/// at least one other, non-Wikipedia provider partition with its own (differently-shaped) title, so there
/// is a genuine second identity to keep. The Wikipedia partition, its external id and its enrichment row
/// move onto a new stub record of their own (lossless -- nothing is discarded); the original re-resolves
/// its title from what remains.
/// </summary>
public sealed class YearOverviewArticleSplitService(
    IServiceScopeFactory scopeFactory,
    ILogger<YearOverviewArticleSplitService> logger)
{
    internal const string WikipediaPluginId = "chronicle.plugin.wikipedia";
    private const string WikipediaSource = "wikipedia";
    private const int PageSize = 500;

    internal static readonly Regex YearOverviewTitle =
        new(@"^\s*\d{4}\s+in\s+(film|television)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Pure detector: the item's Wikipedia partition is titled like "2022 in film", and at least
    /// one other plugin partition has its own title that is NOT the same year-overview shape.</summary>
    internal static bool TryDetectConflict(string? metadataJson, out string wikipediaTitle)
    {
        wikipediaTitle = "";
        if (string.IsNullOrWhiteSpace(metadataJson)) return false;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(metadataJson); }
        catch (JsonException) { return false; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty(WikipediaPluginId, out var wiki) ||
                wiki.ValueKind != JsonValueKind.Object)
                return false;
            if (!MetadataResolutionService.TryGetBlobProperty(wiki, "title", out var wikiTitleEl) ||
                wikiTitleEl.ValueKind != JsonValueKind.String)
                return false;
            var wikiTitle = wikiTitleEl.GetString() ?? "";
            if (!YearOverviewTitle.IsMatch(wikiTitle)) return false;

            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.StartsWith('_') || p.Name == WikipediaPluginId) continue;
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                if (MetadataResolutionService.TryGetBlobProperty(p.Value, "title", out var otherTitle) &&
                    otherTitle.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(otherTitle.GetString()) &&
                    !YearOverviewTitle.IsMatch(otherTitle.GetString()!))
                {
                    wikipediaTitle = wikiTitle;
                    return true;
                }
            }
            return false;
        }
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var resolution = scope.ServiceProvider.GetRequiredService<IMetadataResolutionService>();

        int scanned = 0, split = 0, detached = 0, lastId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await db.MediaItems.AsNoTracking()
                .Where(m => m.Id > lastId && m.HierarchyLevel == 0 &&
                            m.MetadataJson != null && m.MetadataJson.Contains(WikipediaPluginId) &&
                            (m.MetadataJson.Contains(" in film") || m.MetadataJson.Contains(" in television")))
                .OrderBy(m => m.Id).Take(PageSize)
                .Select(m => new { m.Id, m.MetadataJson })
                .ToListAsync(ct);
            if (page.Count == 0) break;
            lastId = page[^1].Id;

            foreach (var row in page)
            {
                scanned++;
                if (!TryDetectConflict(row.MetadataJson, out var wikiTitle)) continue;

                try
                {
                    var created = await SplitAsync(db, resolution, row.Id, wikiTitle, ct);
                    if (created) split++; else detached++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    db.ChangeTracker.Clear();
                    logger.LogWarning(ex, "Year overview article split: item {ItemId} failed, left unchanged", row.Id);
                }
                db.ChangeTracker.Clear();
            }
        }

        logger.LogInformation(
            "Year overview article split: scanned {Scanned} candidate(s) -- split {Split} onto new records, detached {Detached} duplicate attachments",
            scanned, split, detached);
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal async Task<bool> SplitAsync(
        ChronicleDbContext db, IMetadataResolutionService resolution, int itemId, string wikipediaTitle, CancellationToken ct)
    {
        var item = await db.MediaItems.FirstAsync(m => m.Id == itemId, ct);
        var root = JsonNode.Parse(item.MetadataJson ?? "{}") as JsonObject
            ?? throw new InvalidOperationException("MetadataJson is not an object");
        if (root[WikipediaPluginId] is not JsonObject wikiPartition)
            return false; // detector said it existed; concurrent change -- nothing to do

        var wikiExtIds = await db.MediaExternalIds
            .Where(e => e.MediaItemId == itemId && e.Source == WikipediaSource).ToListAsync(ct);
        var wikiEnrichment = await db.MediaEnrichments
            .FirstOrDefaultAsync(e => e.MediaItemId == itemId && e.PluginId == WikipediaPluginId, ct);

        // Already split off this exact article once before? Don't spawn a second stub for it.
        var articleIds = wikiExtIds.Select(e => e.ExternalId).ToList();
        var existingOwner = articleIds.Count == 0 ? null : await db.MediaExternalIds
            .Where(e => e.Source == WikipediaSource && articleIds.Contains(e.ExternalId) && e.MediaItemId != itemId)
            .Select(e => (int?)e.MediaItemId).FirstOrDefaultAsync(ct);

        MediaItem? stub = null;
        if (existingOwner is null)
        {
            stub = new MediaItem
            {
                MediaTypeId    = item.MediaTypeId,
                Name           = wikipediaTitle,
                NormalizedName = MediaItemNormalizer.NormalizeName(wikipediaTitle),
                HierarchyLevel = 0,
                IsStub         = true,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
                MetadataJson   = new JsonObject { [WikipediaPluginId] = wikiPartition.DeepClone() }.ToJsonString(),
            };
            db.MediaItems.Add(stub);
            // No save here: the stub, the re-pointed rows and the original's edit land in ONE
            // SaveChanges, or a failure in between strands an orphan stub.
        }

        root.Remove(WikipediaPluginId);
        item.MetadataJson = root.ToJsonString();
        item.UpdatedAt    = DateTime.UtcNow;

        if (stub is not null)
        {
            foreach (var e in wikiExtIds) e.MediaItem = stub;
            if (wikiEnrichment is not null) wikiEnrichment.MediaItem = stub;
        }
        else
        {
            db.MediaExternalIds.RemoveRange(wikiExtIds);
        }

        if (stub is not null || wikiEnrichment is null)
        {
            db.MediaEnrichments.Add(new MediaItemEnrichment
            {
                MediaItemId = itemId, PluginId = WikipediaPluginId,
                Status = EnrichmentStatus.Pending, MaxRetries = 3,
            });
        }
        else
        {
            // Article already lives elsewhere: park this record's Wikipedia enrichment so the same
            // wrong article can't be re-attached on the next pass and loop.
            wikiEnrichment.Status       = EnrichmentStatus.Skipped;
            wikiEnrichment.ExternalId   = null;
            wikiEnrichment.ErrorMessage = $"Wikipedia article \"{wikipediaTitle}\" is a year-overview page, not this title; owned by item {existingOwner}.";
        }

        await db.SaveChangesAsync(ct);

        await db.Entry(item).Reference(p => p.MediaType).LoadAsync(ct);
        await resolution.ResolveAsync(item, db, ct);
        if (stub is not null)
        {
            await db.Entry(stub).Reference(p => p.MediaType).LoadAsync(ct);
            await resolution.ResolveAsync(stub, db, ct);
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Year overview article split: item {ItemId} \"{OldName}\" -- Wikipedia article \"{WikiTitle}\" is a year overview, not this title; " +
            "now resolves to \"{NewName}\"; {Outcome}",
            itemId, item.Name, wikipediaTitle, item.Name,
            stub is not null ? $"moved onto new item {stub.Id}" : $"detached (already owned by item {existingOwner})");

        return stub is not null;
    }
}
