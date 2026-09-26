using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Scan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Scheduled task that detaches a video file from a movie item whose release year the file's own
/// name flatly contradicts.
///
/// Root-caused live (2026-09-26): "Total Recall (2012).mkv" sat on the 1990 "Total Recall" item, so
/// Kodi showed the 1990 film's plot, cast and poster for the 2012 file (the same class as the
/// Ghostbusters 1984/2016 bug, which was only fixed for the scraper's own search path). A file
/// stays attached forever once recorded: the scan's exact-path tier finds the item that already
/// lists the path and never re-examines it.
///
/// A file whose name says (2012) is not the film an item that is provably from 1990 -- its own
/// provider partitions confirm that year -- describes, so the path is removed from the item's
/// fileScanner.filePaths (and its known-filename rows), and the next scan imports the file again,
/// matching or creating the item for the film it really is. Only a video file, only a year that
/// differs by at least <see cref="MinYearGap"/> (one year is routine release-date noise), and only
/// when a provider partition of the item itself confirms the item's year -- never a guess about
/// which side is wrong.
/// </summary>
public sealed class MovieFileYearRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<MovieFileYearRepairService> logger) : IScheduledTask
{
    public string TaskId      => "movie_file_year_repair";
    public string DisplayName => "Movie File Year Repair";
    public string Description => "Detaches a video file from a movie whose release year the file name contradicts (e.g. \"Total Recall (2012).mkv\" sitting on the 1990 film) so the next scan imports it as the right movie.";
    public string DefaultCron => "30 3 * * *";

    internal const int MinYearGap = 2;
    private const int PageSize = 500;

    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg" };

    private static readonly Regex YearInName = new(@"[\(\[]((?:19|20)\d{2})[\)\]]", RegexOptions.Compiled);

    /// <summary>The (YYYY)/[YYYY] year in a file path's own name, or in its folder name when the
    /// file name has none; null when neither states one.</summary>
    internal static int? YearOfFilePath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        foreach (var candidate in new[] { parts[^1], parts.Length > 1 ? parts[^2] : null })
        {
            if (candidate is null) continue;
            var m = YearInName.Match(candidate);
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        return null;
    }

    /// <summary>True when a provider partition of the item (anything but the file scanner's own or
    /// the reserved keys) states this same year -- proof the ITEM is that year's film.</summary>
    internal static bool ProviderConfirmsYear(string? metadataJson, int year)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return false;
        try
        {
            if (JsonNode.Parse(metadataJson) is not JsonObject root) return false;
            foreach (var (key, node) in root)
            {
                if (key is "fileScanner" or "scraperResolvedFile" or "_resolved" or "_overrides") continue;
                if (key.StartsWith("chronicle_scraper", StringComparison.Ordinal)) continue;
                if (node is not JsonObject partition) continue;
                if (partition["year"] is JsonValue y &&
                    ((y.TryGetValue<int>(out var yi) && yi == year) ||
                     (y.TryGetValue<string>(out var ys) && int.TryParse(ys, out var ysi) && ysi == year)))
                    return true;
            }
        }
        catch (System.Text.Json.JsonException) { }
        return false;
    }

    /// <summary>The file paths of this item that the file's own name contradicts (empty when the
    /// item's year isn't provider-confirmed, or nothing conflicts).</summary>
    internal static List<string> FindContradictedPaths(string? metadataJson, int? itemYear)
    {
        if (itemYear is not { } year) return [];
        var paths = FileIdentityJson.ExtractFilePaths(metadataJson);
        var bad = paths
            .Where(p => VideoExtensions.Contains(Path.GetExtension(p)))
            .Where(p => YearOfFilePath(p) is { } fy && Math.Abs(fy - year) >= MinYearGap)
            .ToList();
        return bad.Count > 0 && ProviderConfirmsYear(metadataJson, year) ? bad : [];
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        int scanned = 0, repaired = 0, detached = 0, lastId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await db.MediaItems
                .Where(m => m.Id > lastId && m.Year != null && m.HierarchyLevel <= 1 &&
                            m.MediaType!.HierarchyLevels == 1 &&
                            m.MetadataJson != null && m.MetadataJson.Contains("filePaths"))
                .OrderBy(m => m.Id).Take(PageSize)
                .ToListAsync(ct);
            if (page.Count == 0) break;
            lastId = page[^1].Id;

            foreach (var item in page)
            {
                scanned++;
                var bad = FindContradictedPaths(item.MetadataJson, item.Year);
                if (bad.Count == 0) continue;

                await DetachAsync(db, item, bad, ct);
                repaired++;
                detached += bad.Count;
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        logger.LogInformation(
            "Movie file year repair: scanned {Scanned} movie(s) -- detached {Files} contradicted file(s) from {Items} item(s); the next scan re-imports them",
            scanned, detached, repaired);
    }

    private async Task DetachAsync(ChronicleDbContext db, MediaItem item, List<string> badPaths, CancellationToken ct)
    {
        var root = JsonNode.Parse(item.MetadataJson!) as JsonObject ?? new JsonObject();
        var badSet = badPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var badNames = badPaths.Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (root["fileScanner"] is JsonObject scanner && scanner["filePaths"] is JsonArray paths)
        {
            var kept = paths.Where(n => n is JsonValue v && v.TryGetValue<string>(out var s) && !badSet.Contains(s!))
                .Select(n => n!.DeepClone()).ToList();
            scanner["filePaths"] = new JsonArray(kept.ToArray());

            // The folder recorded for the detached file is stale too -- keeping it would leave the
            // item pointing at the wrong film's folder.
            if (scanner["folderPath"] is JsonValue fp && fp.TryGetValue<string>(out var folder) &&
                YearOfFilePath(folder) is { } folderYear && item.Year is { } itemYear &&
                Math.Abs(folderYear - itemYear) >= MinYearGap)
                scanner["folderPath"] = null;
        }
        if (root["scraperResolvedFile"]?["fileName"] is JsonValue rf && rf.TryGetValue<string>(out var resolvedName) &&
            badNames.Contains(resolvedName))
            root.Remove("scraperResolvedFile");

        item.MetadataJson = root.ToJsonString();
        item.UpdatedAt = DateTime.UtcNow;

        var known = await db.MediaItemKnownFileNames
            .Where(k => k.MediaItemId == item.Id).ToListAsync(ct);
        db.MediaItemKnownFileNames.RemoveRange(known.Where(k => badNames.Contains(k.FileName)));

        logger.LogInformation(
            "Movie file year repair: item {ItemId} \"{Name}\" ({Year}) -- detached {Files}: the file name's year contradicts a provider-confirmed year",
            item.Id, item.Name, item.Year, string.Join(", ", badNames));
    }
}
