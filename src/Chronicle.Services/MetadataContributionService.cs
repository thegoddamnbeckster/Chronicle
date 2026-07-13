using System.Text.Json;
using System.Text.Json.Nodes;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Scan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Accepts metadata contributions from any authenticated external caller — not tied to any
/// one integration — following Chronicle's lossless-ingestion principle: a source's data
/// lands in its own metadata_json partition, untouched by (and never overwriting) any other
/// source's partition.
/// </summary>
public class MetadataContributionService(
    IMetadataResolutionService resolutionService,
    TagMismatchRematchQueue rematchQueue,
    ILogger<MetadataContributionService> logger) : IMetadataContributionService
{
    private static readonly HashSet<string> ReservedSourceKeys =
        new(StringComparer.OrdinalIgnoreCase) { "_resolved", "fileScanner" };

    public async Task<ContributionOutcome> ContributeAsync(
        MediaItem item,
        ChronicleDbContext db,
        string source,
        JsonElement metadataPayload,
        FileIdentitySnapshot? file,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source) || ReservedSourceKeys.Contains(source))
            return ContributionOutcome.Fail("RESERVED_SOURCE",
                $"'{source}' is a reserved key and cannot be used as a contribution source.");

        if (metadataPayload.ValueKind != JsonValueKind.Object || !metadataPayload.EnumerateObject().Any())
            return ContributionOutcome.Fail("EMPTY_PAYLOAD", "metadata must be a non-empty object.");

        if (item.MediaType is null)
            await db.Entry(item).Reference(m => m.MediaType).LoadAsync(ct);

        var blobs = MetadataResolutionService.ParsePluginBlobs(item.MetadataJson);

        // Snapshot the pre-merge resolved view before anything is mutated — this is what a
        // mismatch is measured against, since ResolveAsync overwrites "_resolved" below.
        Dictionary<string, JsonElement>? previousResolved = null;
        if (blobs.TryGetValue("_resolved", out var prevResolvedEl) && prevResolvedEl.ValueKind == JsonValueKind.Object)
            previousResolved = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(prevResolvedEl.GetRawText());

        // The MergeMetadata pattern (MetadataEnrichmentService): set only this source's own
        // top-level key, re-serialize the whole dict — every other source's partition is
        // preserved untouched.
        blobs[source] = metadataPayload;

        var fingerprintChanged = false;
        if (file is not null)
        {
            var fileScannerNode = blobs.TryGetValue("fileScanner", out var fsEl) && fsEl.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(fsEl.GetRawText()) as JsonObject ?? new JsonObject()
                : new JsonObject();

            fingerprintChanged = FileIdentityJson.ApplyIfChanged(fileScannerNode, file);
            blobs["fileScanner"] = JsonSerializer.SerializeToElement(fileScannerNode);
        }

        item.MetadataJson = JsonSerializer.Serialize(blobs);

        await resolutionService.ResolveAsync(item, db, ct);
        await db.SaveChangesAsync(ct);

        var tagMismatchDetected = DetectTagMismatch(metadataPayload, previousResolved);

        var rematchQueued = false;
        if (tagMismatchDetected)
        {
            var setting = await db.AppSettings.FindAsync(["auto_rematch_on_tag_mismatch"], ct);
            var autoRematchEnabled = setting is not null && bool.TryParse(setting.Value, out var enabled) && enabled;
            if (autoRematchEnabled)
                rematchQueued = rematchQueue.TryEnqueue(item.Id);
        }

        logger.LogInformation(
            "Contribution from '{Source}' merged into item {Id}: fingerprintChanged={FpChanged}, tagMismatch={Mismatch}, rematchQueued={Queued}",
            source, item.Id, fingerprintChanged, tagMismatchDetected, rematchQueued);

        return new ContributionOutcome(true, null, null, fingerprintChanged, tagMismatchDetected, rematchQueued);
    }

    /// <summary>
    /// Compares contributed fields against Chronicle's previously-resolved view, only for keys
    /// present in both. A field contributed for the first time never "mismatches" — there was
    /// nothing to disagree with.
    /// </summary>
    private static bool DetectTagMismatch(JsonElement metadataPayload, Dictionary<string, JsonElement>? previousResolved)
    {
        if (previousResolved is null || previousResolved.Count == 0) return false;

        foreach (var prop in metadataPayload.EnumerateObject())
        {
            if (!previousResolved.TryGetValue(prop.Name, out var existingVal)) continue;
            if (!MetadataResolutionService.HasValue(existingVal)) continue;
            if (!MetadataResolutionService.HasValue(prop.Value)) continue;
            if (!ValuesEqual(prop.Value, existingVal)) return true;
        }
        return false;
    }

    private static bool ValuesEqual(JsonElement a, JsonElement b) =>
        string.Equals(NormalizeForCompare(a), NormalizeForCompare(b), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeForCompare(JsonElement el) =>
        el.ValueKind == JsonValueKind.String ? (el.GetString() ?? string.Empty).Trim() : el.GetRawText();
}
