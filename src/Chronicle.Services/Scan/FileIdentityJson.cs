using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    }
}
