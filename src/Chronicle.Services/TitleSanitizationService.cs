using System.Text.Json;
using System.Text.Json.Nodes;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Nightly scheduled task that strips a stray trailing parenthetical (e.g. "(film)", "(actor)",
/// "(3 Doors Down song)") from a configured MetadataJson partition field, then recomputes the
/// affected item's resolved metadata -- a standing safety net, not a one-off backfill.
///
/// Why this exists as an ongoing task rather than another manual fix: per-user request
/// (2026-09-05), after the SAME class of Wikipedia-disambiguator-suffix bug needed fixing a
/// third time -- first a code fix (WikipediaMetadataProvider now correctly strips at write
/// time), then a one-off backlog reprocess (2668 items), then a THIRD manual pass (this
/// session, 4568 more items -- the earlier backlog detection query never covered the "music"
/// media type at all, which turned out to be 82% of what was still wrong). Two backfills in a
/// row each left a gap; nothing was ever left running to catch what either one missed, or what
/// a future regression, plugin downgrade, manual edit, or third-party `contribute_metadata`
/// call might reintroduce. This task is that missing standing check.
///
/// Deliberately DB-configurable (title_sanitization.config in app_settings), not hardcoded to
/// "chronicle.plugin.wikipedia" in code -- consistent with this codebase's existing
/// metadata_assignment.config pattern, and because the actual operation (strip a trailing
/// parenthetical) is generic string processing, not something that requires knowing Wikipedia's
/// specific naming convention at compile time. Falls back to a sensible default (Wikipedia's
/// own title field) only when nothing has been configured yet, so this works out of the box
/// without requiring a manual setup step.
/// </summary>
public sealed class TitleSanitizationService(
    IServiceScopeFactory scopeFactory,
    ILogger<TitleSanitizationService> logger) : IScheduledTask
{
    public string TaskId      => "title_sanitization";
    public string DisplayName => "Title Sanitization";
    public string Description => "Strips a stray trailing parenthetical (e.g. a Wikipedia disambiguator like \"(film)\" or \"(actor)\") left behind in a plugin's stored title, and recomputes affected items' resolved metadata. Configurable via app_settings key title_sanitization.config.";
    public string DefaultCron => "30 4 * * *"; // 4:30 AM, just after MissingSourceReconciliationService's 4 AM run

    private const string ConfigKey = "title_sanitization.config";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db         = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var resolution = scope.ServiceProvider.GetRequiredService<IMetadataResolutionService>();

        var targets = await LoadConfigAsync(db, ct);
        if (targets.Count == 0)
        {
            logger.LogInformation("Title sanitization: no partition/field pairs configured -- nothing to check.");
            return;
        }

        // Candidates: any item whose MetadataJson mentions a configured partition key. One
        // simple, literal-pattern LIKE query per distinct partition key (not one query with a
        // per-row-computed pattern -- EF can't translate that) avoids deserializing every item
        // in the catalog just to find the handful that could possibly match.
        var candidates = new List<MediaItem>();
        var seenIds = new HashSet<int>();
        foreach (var partitionKey in targets.Select(t => t.PartitionKey).Distinct())
        {
            var pattern = $"%{partitionKey}%";
            var matches = await db.MediaItems
                .Where(m => m.MetadataJson != null && EF.Functions.Like(m.MetadataJson, pattern))
                .ToListAsync(ct);
            foreach (var m in matches)
                if (seenIds.Add(m.Id)) candidates.Add(m);
        }

        var fixedCount = 0;
        foreach (var item in candidates)
        {
            ct.ThrowIfCancellationRequested();

            JsonObject root;
            try
            {
                root = JsonNode.Parse(item.MetadataJson!)?.AsObject() ?? new JsonObject();
            }
            catch (JsonException)
            {
                continue; // malformed blob -- not this task's job to repair
            }

            var changed = false;
            foreach (var (partitionKey, field) in targets)
            {
                if (root[partitionKey] is not JsonObject partition) continue;
                var value = partition[field]?.GetValue<string>();
                if (string.IsNullOrEmpty(value)) continue;

                var stripped = TrailingParentheticalRe.Replace(value, string.Empty);
                if (stripped == value) continue;

                partition[field] = stripped;
                changed = true;
            }
            if (!changed) continue;

            item.MetadataJson = root.ToJsonString();
            await resolution.ResolveAsync(item, db, ct);
            fixedCount++;

            logger.LogInformation(
                "Title sanitization: item {ItemId} \"{Name}\" -- stripped a stray trailing parenthetical.",
                item.Id, item.Name);
        }

        if (fixedCount > 0)
            await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Title sanitization complete: checked {Checked} candidate(s), fixed {Fixed}.",
            candidates.Count, fixedCount);
    }

    /// <summary>Matches the same trailing-parenthetical shape Wikipedia disambiguation uses --
    /// "(film)", "(1982 film)", "(TV series)", "(3 Doors Down song)" -- kept in sync with
    /// Chronicle.Plugin.Wikipedia's own WikipediaScoring.StripDisambiguationSuffix by
    /// construction (same pattern, copied deliberately rather than referenced: Chronicle core
    /// has no compile-time reference to plugin assemblies -- plugins are loaded dynamically,
    /// see CLAUDE.md's "Plugins live in their own repos"). This is a generic string operation,
    /// not Wikipedia-specific knowledge; which partition/field it's applied to is what's
    /// actually configurable, via title_sanitization.config below.</summary>
    private static readonly System.Text.RegularExpressions.Regex TrailingParentheticalRe =
        new(@"\s*\([^)]*\)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private sealed record SanitizationTarget(string PartitionKey, string Field);

    private static async Task<List<SanitizationTarget>> LoadConfigAsync(ChronicleDbContext db, CancellationToken ct)
    {
        var setting = await db.AppSettings.FindAsync(["title_sanitization.config"], ct);
        if (setting?.Value is null)
        {
            // Default, seeded only when nothing has ever been configured -- an admin can add,
            // remove, or clear entries via app_settings without a code change (same pattern as
            // metadata_assignment.config).
            return [new SanitizationTarget("chronicle.plugin.wikipedia", "title")];
        }

        try
        {
            var pairs = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(setting.Value) ?? [];
            return pairs
                .Where(p => p.ContainsKey("partition") && p.ContainsKey("field"))
                .Select(p => new SanitizationTarget(p["partition"], p["field"]))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
