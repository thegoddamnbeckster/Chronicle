using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Chronicle.Services.Scan
{
    /// <summary>Technical/identity data about a file, reported by a scanner or an external contributor.</summary>
    public sealed record FileIdentitySnapshot(
        long? SizeBytes,
        DateTime? ModifiedUtc,
        int? BitrateKbps,
        int? SampleRateHz,
        int? DurationSeconds,
        string? FileType);

    /// <summary>
    /// Reads/writes the technical-identity fields (fingerprint, size, bitrate, sample rate,
    /// duration, file type) inside a MediaItem's "fileScanner" MetadataJson partition.
    /// Shared by the metadata contribution endpoint and (eventually) FileScanService's own
    /// writers, so both never diverge on field names.
    /// </summary>
    public static class FileIdentityJson
    {
        /// <summary>
        /// Cheap fingerprint — size + modified-time. Deliberately not a full content hash:
        /// computing one on every "now playing" event would add real I/O cost for little gain.
        /// </summary>
        public static string ComputeFingerprint(long? sizeBytes, DateTime? modifiedUtc) =>
            $"{sizeBytes ?? 0}:{modifiedUtc?.ToUniversalTime().Ticks ?? 0}";

        /// <summary>
        /// Merges a snapshot into the given "fileScanner" JsonObject unconditionally.
        /// Returns true when the computed fingerprint differs from what was already stored —
        /// i.e. the file genuinely changed since the last report.
        /// </summary>
        public static bool ApplyIfChanged(JsonObject fileScannerNode, FileIdentitySnapshot snapshot)
        {
            var newFingerprint = ComputeFingerprint(snapshot.SizeBytes, snapshot.ModifiedUtc);
            var oldFingerprint = fileScannerNode["fingerprint"]?.GetValue<string>();
            var changed = !string.Equals(oldFingerprint, newFingerprint, StringComparison.Ordinal);

            fileScannerNode["fingerprint"]      = newFingerprint;
            fileScannerNode["fileSizeBytes"]    = snapshot.SizeBytes;
            fileScannerNode["fileModifiedUtc"]  = snapshot.ModifiedUtc;
            fileScannerNode["bitrateKbps"]      = snapshot.BitrateKbps;
            fileScannerNode["sampleRateHz"]     = snapshot.SampleRateHz;
            fileScannerNode["durationSeconds"]  = snapshot.DurationSeconds;
            fileScannerNode["fileType"]         = snapshot.FileType;

            return changed;
        }

        /// <summary>
        /// Extracts every path in the "fileScanner.filePaths" array of a MetadataJson blob.
        /// Empty when absent, empty, or malformed. This is the single canonical reader for
        /// physical-file identity — every writer (flat scan, direct import, hierarchical
        /// group scan) must serialize this same "filePaths" array shape so all matching code
        /// (scan de-dup, DuplicateCleanupService) agrees on what a file's identity is.
        /// </summary>
        public static IReadOnlyList<string> ExtractFilePaths(string? metadataJson)
        {
            if (string.IsNullOrEmpty(metadataJson)) return [];
            try
            {
                using var doc = JsonDocument.Parse(metadataJson);
                if (!doc.RootElement.TryGetProperty("fileScanner", out var scanner)) return [];
                if (!scanner.TryGetProperty("filePaths", out var fps) || fps.ValueKind != JsonValueKind.Array)
                    return [];

                return fps.EnumerateArray()
                    .Select(el => el.ValueKind == JsonValueKind.String ? el.GetString() : null)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Select(s => s!)
                    .ToList();
            }
            catch (JsonException)
            {
                return [];
            }
        }

        /// <summary>
        /// Deterministic grouping key for a set of file paths — the lexicographically-first
        /// path (case-insensitive), independent of array insertion order. Two items scanned
        /// with the same file set listed in a different order still resolve to the same key.
        /// Returns null when the item has no file paths recorded.
        /// </summary>
        public static string? PrimaryFilePathKey(string? metadataJson)
        {
            var paths = ExtractFilePaths(metadataJson);
            return paths.Count == 0
                ? null
                : paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).First();
        }

        /// <summary>
        /// True when <paramref name="filePath"/> exactly matches (case-insensitive) any entry
        /// in the blob's "fileScanner.filePaths" array.
        /// </summary>
        public static bool ContainsFilePath(string? metadataJson, string filePath) =>
            ExtractFilePaths(metadataJson).Any(p => string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// True when any path in <paramref name="candidatePaths"/> exactly matches
        /// (case-insensitive) any entry in the blob's "fileScanner.filePaths" array.
        /// </summary>
        public static bool ContainsAnyFilePath(string? metadataJson, IEnumerable<string> candidatePaths)
        {
            var stored = ExtractFilePaths(metadataJson);
            if (stored.Count == 0) return false;
            var storedSet = stored.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return candidatePaths.Any(storedSet.Contains);
        }

        /// <summary>
        /// True if Chronicle has ANY record of a real physical file for this item -- either
        /// from its own file scanner ("fileScanner.filePaths", the authoritative source used
        /// everywhere above) or from a Kodi scraper's own filesystem discovery reported back
        /// after the fact ("scraperResolvedFile.fileName" -- a weaker signal, a bare filename
        /// with no directory, not independently verified by Chronicle -- but still real: Kodi
        /// found this exact file on disk, it just never went through Chronicle's own directory
        /// scan). Deliberately NOT folded into ExtractFilePaths/PrimaryFilePathKey/
        /// ContainsAnyFilePath above -- those exist specifically for exact-path duplicate
        /// matching (DuplicateCleanupService, scan de-dup), where a bare filename with no
        /// directory context is a meaningfully weaker, riskier signal than a verified full
        /// path. This is the broader "do I actually own a file for this" signal instead, for
        /// ownership displays (library grid, collection membership) -- not for merge decisions.
        /// </summary>
        public static bool HasKnownFile(string? metadataJson) =>
            GetKnownFileName(metadataJson) is not null;

        /// <summary>
        /// The real video file's own basename (with extension) for this item, if Chronicle
        /// knows one -- preferring its own file scanner's record
        /// ("fileScanner.filePaths[0]", the higher-confidence source: a full directory scan,
        /// not a single search-time guess) and falling back to a Kodi scraper's own filesystem
        /// discovery reported back after the fact ("scraperResolvedFile.fileName"). Null when
        /// Chronicle has no file record for this item at all. The single reader behind both
        /// HasKnownFile above and ScraperController's KnownFileName field for Kodi -- previously
        /// two independent hand-rolled copies of this exact fileScanner-then-scraperResolvedFile
        /// fallback (one of which, until now, only ever checked the first half).
        /// </summary>
        public static string? GetKnownFileName(string? metadataJson)
        {
            var paths = ExtractFilePaths(metadataJson);
            if (paths.Count > 0)
            {
                var name = paths[0].Split('\\', '/').LastOrDefault();
                if (!string.IsNullOrEmpty(name)) return name;
            }

            if (string.IsNullOrEmpty(metadataJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(metadataJson);
                if (doc.RootElement.TryGetProperty("scraperResolvedFile", out var srf) &&
                    srf.ValueKind == JsonValueKind.Object &&
                    srf.TryGetProperty("fileName", out var fn) &&
                    fn.ValueKind == JsonValueKind.String)
                    return fn.GetString();
            }
            catch (JsonException) { }
            return null;
        }

        /// <summary>
        /// True when <paramref name="fileName"/>'s own text contains <paramref name="title"/>,
        /// compared letters/digits-only and case-insensitively (mirrors
        /// DuplicateCandidateScanService.LogShowPathMismatchesAsync's own normalization). Used
        /// to tell whether an item's own recorded file was actually scanned FOR this item (a
        /// directly-verified signal), versus a stale/corrupted association pointing elsewhere --
        /// see MergeService.MergeLoadedItemsAsync and DuplicateCleanupService's same-parent pass,
        /// both of which trust the side whose file matches its title over the side whose doesn't.
        /// </summary>
        public static bool FileNameMatchesTitle(string? fileName, string title)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            var normalizedTitle = Normalize(title);
            return normalizedTitle.Length > 0 && Normalize(fileName).Contains(normalizedTitle, StringComparison.Ordinal);
        }

        private static readonly Regex SeasonEpisodeInFileNameRegex =
            new(@"[Ss](\d{1,2})[Ee](\d{1,3})", RegexOptions.Compiled);

        private static readonly Regex SeasonEpisodeInExternalIdRegex =
            new(@"season:(\d+)/episode:(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Season/episode parsed from a "SxxEyy"-style filename fragment, if present.</summary>
        public static (int Season, int Episode)? ExtractSeasonEpisodeFromFileName(string? fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            var m = SeasonEpisodeInFileNameRegex.Match(fileName);
            return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
        }

        /// <summary>
        /// Season/episode parsed from the first external id (of any source) that encodes one
        /// in the "season:N/episode:M" shape this codebase's own tmdb ids already use (see
        /// ScraperController's own extendedData.seasonNumber/episodeNumber). Other sources
        /// (e.g. a bare tvmaze "episode:12345") don't encode this and are silently skipped.
        /// </summary>
        public static (int Season, int Episode)? ExtractSeasonEpisodeFromExternalIds(
            IEnumerable<Chronicle.Core.Models.MediaExternalId> externalIds)
        {
            foreach (var id in externalIds)
            {
                var m = SeasonEpisodeInExternalIdRegex.Match(id.ExternalId ?? string.Empty);
                if (m.Success) return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            }
            return null;
        }

        /// <summary>
        /// Whether the file recorded in <paramref name="candidateMetadataJson"/> genuinely
        /// belongs to <paramref name="referenceItem"/>'s identity -- checked two ways: does
        /// the file's own name contain the reference item's title (works whenever every
        /// source agrees on the episode's title), or failing that, does the file's own
        /// season/episode code (e.g. "S14E01") match a season/episode parsed from the
        /// reference item's own external ids (works even when different metadata providers
        /// give the same episode different titles -- confirmed live 2026-09-21: TMDB titled
        /// an episode "Celebrity: A La Cuisine!", TVMAZE titled the exact same episode "By
        /// Land and Sea", and the real file on disk followed TVMAZE's title, so a pure
        /// title-text check found no match even though the file was genuinely correct).
        /// Pass the SAME item as both the file source and the reference to check a side
        /// against its own identity; pass a DIFFERENT item (e.g. the merge winner) to check
        /// one side's file against the other side's identity, for a stub with no identity of
        /// its own to compare against.
        /// </summary>
        public static bool FileBelongsTo(
            string? candidateMetadataJson, string referenceName,
            IEnumerable<Chronicle.Core.Models.MediaExternalId> referenceExternalIds)
        {
            var fileName = GetKnownFileName(candidateMetadataJson);
            if (fileName is null) return false;
            if (FileNameMatchesTitle(fileName, referenceName)) return true;

            var fileSeasonEpisode = ExtractSeasonEpisodeFromFileName(fileName);
            if (fileSeasonEpisode is null) return false;
            return fileSeasonEpisode == ExtractSeasonEpisodeFromExternalIds(referenceExternalIds);
        }
    }
}
