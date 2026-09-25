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
/// Scheduled task that splits a "people" record which has been welded onto TWO different real
/// people: a name-searched Wikipedia article whose birth year provably disagrees with the birth
/// year another provider (TMDB, whose person id arrives inside a credit list on a title we
/// hold) recorded for the same item.
///
/// Root-caused live (2026-09-24): Chris Evans (the actor, TMDB 1981) carried the Wikipedia
/// article of Chris Evans the English presenter (1966); Phil Davis the MMA fighter (1984) carried
/// the article of Phil Davis the actor (1953); Reverend Gary Davis and John D'Aquino carried a
/// TMDB entry for a different person than their Wikipedia article. Headshots from every attached
/// provider are pooled and the newest wins, so the wrong person's photo showed up as the main
/// photo or in the gallery. See PersonResolutionService's own docs for how the id gets attached.
///
/// Policy (the user's rule): when the evidence conflicts, never keep guessing one merged record.
/// The record keeps the credit-backed provider data (TMDB's ids and the credits stay put); the
/// Wikipedia article, its photos, its metadata partition and its enrichment row move onto a NEW
/// person record of their own, and the original's Wikipedia enrichment is reset so it searches
/// again, now armed with the trusted birth year (WikipediaScoring rejects a mismatched year).
/// Only a PROVABLE conflict is acted on: both a trusted birth year and a Wikipedia "YYYY births"
/// category must exist and differ by at least <see cref="MinYearGap"/> (a one-year gap is routine
/// timezone/estimate noise between sources, not a different person).
///
/// Repeatable by design: runs on a schedule, so a fresh wrong attachment is caught the next night
/// rather than needing anyone to notice it. If a later enrichment re-attaches the very article
/// that was split off, a second stub is NOT created -- the duplicate attachment is just detached
/// and the record's Wikipedia enrichment is parked as Skipped so the pair can't loop.
/// </summary>
public sealed class PersonIdentitySplitService(
    IServiceScopeFactory scopeFactory,
    ILogger<PersonIdentitySplitService> logger) : IScheduledTask
{
    public string TaskId      => "person_identity_split";
    public string DisplayName => "Person Identity Split";
    public string Description => "Finds people whose Wikipedia article is provably a different person than their credit-backed provider data (birth years disagree) and moves the article, its photos and its data onto a separate person record.";
    public string DefaultCron => "45 2 * * *";

    /// <summary>Minimum birth-year difference treated as two different people.</summary>
    internal const int MinYearGap = 2;

    internal const string WikipediaPluginId = "chronicle.plugin.wikipedia";
    private const string WikipediaSource = "wikipedia";
    private const int PageSize = 500;

    private static readonly Regex BirthsTag = new(@"^\s*(\d{4})\s+births\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Pure detector. Returns true when <paramref name="metadataJson"/> holds a Wikipedia partition
    /// with a "YYYY births" category AND at least one other provider partition with a birthDate,
    /// all other-provider years agree with each other, and the Wikipedia year differs from them
    /// by <see cref="MinYearGap"/> or more.
    /// </summary>
    internal static bool TryDetectConflict(string? metadataJson, out int trustedYear, out int wikipediaYear)
    {
        trustedYear = wikipediaYear = 0;
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

            int? wikiYear = null;
            if (wiki.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tags.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.String) continue;
                    var m = BirthsTag.Match(t.GetString() ?? "");
                    if (m.Success) { wikiYear = int.Parse(m.Groups[1].Value); break; }
                }
            }
            if (wikiYear is null) return false;

            var trustedYears = new HashSet<int>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.StartsWith('_') || p.Name == WikipediaPluginId) continue;
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                if (MetadataResolutionService.TryGetBlobProperty(p.Value, "birthDate", out var bd) &&
                    bd.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(bd.GetString(), out var when))
                    trustedYears.Add(when.Year);
            }

            // Other providers disagreeing among themselves means the evidence is muddled in a way
            // this task can't arbitrate -- leave for a human, don't guess.
            if (trustedYears.Count != 1) return false;

            trustedYear   = trustedYears.Single();
            wikipediaYear = wikiYear.Value;
            return Math.Abs(trustedYear - wikipediaYear) >= MinYearGap;
        }
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var resolution = scope.ServiceProvider.GetRequiredService<IMetadataResolutionService>();

        var peopleTypeId = await db.MediaTypes
            .Where(t => t.Name == "people").Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);
        if (peopleTypeId is null)
        {
            logger.LogInformation("Person identity split: no 'people' media type exists yet, nothing to do");
            return;
        }

        int scanned = 0, split = 0, detached = 0, lastId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await db.MediaItems.AsNoTracking()
                .Where(m => m.MediaTypeId == peopleTypeId && m.Id > lastId &&
                            m.MetadataJson != null && m.MetadataJson.Contains(WikipediaPluginId))
                .OrderBy(m => m.Id).Take(PageSize)
                .Select(m => new { m.Id, m.MetadataJson })
                .ToListAsync(ct);
            if (page.Count == 0) break;
            lastId = page[^1].Id;

            foreach (var row in page)
            {
                scanned++;
                if (!TryDetectConflict(row.MetadataJson, out var trusted, out var wiki)) continue;

                try
                {
                    var created = await SplitAsync(db, resolution, row.Id, trusted, wiki, ct);
                    if (created) split++; else detached++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    db.ChangeTracker.Clear();
                    logger.LogWarning(ex, "Person identity split: person {PersonId} failed, left unchanged", row.Id);
                }
                db.ChangeTracker.Clear();
            }
        }

        logger.LogInformation(
            "Person identity split: scanned {Scanned} people with a Wikipedia partition -- split {Split} onto new records, detached {Detached} duplicate attachments",
            scanned, split, detached);
    }

    /// <summary>The string value of a JSON node, or null when it is absent or not a string --
    /// GetValue&lt;string&gt;() throws on a number/object and would make one odd row fail every night.</summary>
    private static string? StringOf(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Returns true when a new person record was created, false when the article was
    /// already owned by another record and was only detached from this one.</summary>
    internal async Task<bool> SplitAsync(
        ChronicleDbContext db, IMetadataResolutionService resolution,
        int personId, int trustedYear, int wikipediaYear, CancellationToken ct)
    {
        var person = await db.MediaItems.FirstAsync(m => m.Id == personId, ct);
        var root = JsonNode.Parse(person.MetadataJson ?? "{}") as JsonObject
            ?? throw new InvalidOperationException("MetadataJson is not an object");
        if (root[WikipediaPluginId] is not JsonObject wikiPartition)
            return false; // detector said it existed; concurrent change -- nothing to do

        var wikiExtIds = await db.MediaExternalIds
            .Where(e => e.MediaItemId == personId && e.Source == WikipediaSource).ToListAsync(ct);
        var wikiHeadshots = await db.PersonHeadshots
            .Where(h => h.PersonMediaItemId == personId && h.Source == WikipediaSource).ToListAsync(ct);
        var wikiEnrichment = await db.MediaEnrichments
            .FirstOrDefaultAsync(e => e.MediaItemId == personId && e.PluginId == WikipediaPluginId, ct);

        // Already split once before? Then the article has a home of its own -- don't spawn a
        // second stub for it, just detach it from this record again.
        var articleIds = wikiExtIds.Select(e => e.ExternalId).ToList();
        var existingOwner = articleIds.Count == 0 ? null : await db.MediaExternalIds
            .Where(e => e.Source == WikipediaSource && articleIds.Contains(e.ExternalId) && e.MediaItemId != personId)
            .Select(e => (int?)e.MediaItemId).FirstOrDefaultAsync(ct);

        MediaItem? stub = null;
        JsonObject? stubOverrides = null;
        var movedUrls = wikiHeadshots.Select(h => h.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (StringOf(wikiPartition["posterUrl"]) is { Length: > 0 } wikiPoster)
            movedUrls.Add(wikiPoster);

        if (existingOwner is null)
        {
            var stubName = StringOf(wikiPartition["title"]) is { Length: > 0 } t ? t : person.Name;
            stub = new MediaItem
            {
                MediaTypeId    = person.MediaTypeId,
                Name           = stubName,
                NormalizedName = MediaItemNormalizer.NormalizeName(stubName),
                HierarchyLevel = 0,
                IsStub         = true,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            };
            var stubRoot = new JsonObject { [WikipediaPluginId] = wikiPartition.DeepClone() };

            // A poster/image pin the user made onto one of the moving photos goes with the photo.
            if (root["_overrides"] is JsonObject overrides)
            {
                stubOverrides = new JsonObject();
                foreach (var key in overrides.Select(kv => kv.Key).ToList())
                {
                    var url = overrides[key] is JsonObject o ? StringOf(o["url"]) : null;
                    if (url is not null && movedUrls.Contains(url))
                    {
                        stubOverrides[key] = overrides[key]!.DeepClone();
                        overrides.Remove(key);
                    }
                }
                if (stubOverrides.Count > 0) stubRoot["_overrides"] = stubOverrides;
                if (overrides.Count == 0) root.Remove("_overrides");
            }

            stub.MetadataJson = stubRoot.ToJsonString();
            db.MediaItems.Add(stub);
            // No save here: the stub, the re-pointed rows and the original's edit must land in ONE
            // SaveChanges, or a failure in between strands an orphan stub and the next nightly run
            // (which still sees the article on the original) would create another.
        }
        else if (root["_overrides"] is JsonObject overrides)
        {
            // Drop pins that pointed at the departing photos; the article's home keeps its own.
            foreach (var key in overrides.Select(kv => kv.Key).ToList())
            {
                var url = overrides[key] is JsonObject o ? StringOf(o["url"]) : null;
                if (url is not null && movedUrls.Contains(url)) overrides.Remove(key);
            }
            if (overrides.Count == 0) root.Remove("_overrides");
        }

        root.Remove(WikipediaPluginId);
        person.MetadataJson = root.ToJsonString();
        person.UpdatedAt    = DateTime.UtcNow;

        if (stub is not null)
        {
            foreach (var e in wikiExtIds) e.MediaItem = stub;
            foreach (var h in wikiHeadshots) h.PersonMediaItem = stub;
            if (wikiEnrichment is not null) wikiEnrichment.MediaItem = stub;
        }
        else
        {
            db.MediaExternalIds.RemoveRange(wikiExtIds);
            db.PersonHeadshots.RemoveRange(wikiHeadshots);
        }

        if (stub is not null || wikiEnrichment is null)
        {
            // Fresh Pending row: search again, this time with the trusted birth year as a hard hint.
            db.MediaEnrichments.Add(new MediaItemEnrichment
            {
                MediaItemId = personId, PluginId = WikipediaPluginId,
                Status = EnrichmentStatus.Pending, MaxRetries = 3,
            });
        }
        else
        {
            // Article already lives elsewhere: park this record's Wikipedia enrichment so the
            // same wrong article can't be re-attached on the next pass and loop.
            wikiEnrichment.Status       = EnrichmentStatus.Skipped;
            wikiEnrichment.ExternalId   = null;
            wikiEnrichment.ErrorMessage = $"Wikipedia article born {wikipediaYear} conflicts with trusted birth year {trustedYear}; owned by person {existingOwner}.";
        }

        await db.SaveChangesAsync(ct);

        await db.Entry(person).Reference(p => p.MediaType).LoadAsync(ct);
        await resolution.ResolveAsync(person, db, ct);
        if (stub is not null)
        {
            await db.Entry(stub).Reference(p => p.MediaType).LoadAsync(ct);
            await resolution.ResolveAsync(stub, db, ct);
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Person identity split: person {PersonId} \"{Name}\" -- Wikipedia article (born {WikiYear}) conflicts with trusted birth year {Trusted}; " +
            "{Outcome}",
            personId, person.Name, wikipediaYear, trustedYear,
            stub is not null ? $"moved onto new person {stub.Id}" : $"detached (already owned by person {existingOwner})");

        return stub is not null;
    }
}
