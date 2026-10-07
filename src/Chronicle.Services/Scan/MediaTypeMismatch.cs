using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Core.Models;
using Chronicle.Core.Models.Scan;

namespace Chronicle.Services.Scan
{
    /// <summary>What files of a media type look like, parsed from <c>media_types.ScanHintsJson</c>. Patterns are
    /// "distinctive" (an S01E02 file name); extensions are "broad" (every video is .mkv).</summary>
    public sealed class ScanHints
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        public IReadOnlyList<Regex> FilePatterns { get; }
        public IReadOnlyList<Regex> FolderPatterns { get; }
        public IReadOnlySet<string> Extensions { get; }
        public bool IsEmpty => FilePatterns.Count == 0 && FolderPatterns.Count == 0 && Extensions.Count == 0;

        private ScanHints(List<Regex> files, List<Regex> folders, HashSet<string> exts)
        {
            FilePatterns = files; FolderPatterns = folders; Extensions = exts;
        }

        private sealed record Raw(List<string>? FilePatterns, List<string>? FolderPatterns, List<string>? Extensions);

        /// <summary>Null when the value is empty or not usable. Bad individual patterns are skipped, never thrown.</summary>
        public static ScanHints? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var raw = JsonSerializer.Deserialize<Raw>(json, Json);
                if (raw is null) return null;
                var hints = new ScanHints(Compile(raw.FilePatterns), Compile(raw.FolderPatterns),
                    new HashSet<string>((raw.Extensions ?? []).Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase));
                return hints.IsEmpty ? null : hints;
            }
            catch (JsonException) { return null; }
        }

        /// <summary>Null when valid, otherwise a sentence an administrator can act on. Empty input is valid (no hints).</summary>
        public static string? Validate(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            if (json.Length > 4000) return "The scan hints are too long (4000 characters at most).";
            Raw? raw;
            try { raw = JsonSerializer.Deserialize<Raw>(json, Json); }
            catch (JsonException) { return "The scan hints must be JSON like {\"filePatterns\":[],\"folderPatterns\":[],\"extensions\":[\".mkv\"]}."; }
            if (raw is null) return "The scan hints must be a JSON object.";
            foreach (var p in (raw.FilePatterns ?? []).Concat(raw.FolderPatterns ?? []))
            {
                try { _ = new Regex(p, RegexOptions.NonBacktracking, RegexTimeout); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                { return $"The pattern \"{p}\" is not a usable regular expression."; }
            }
            if ((raw.Extensions ?? []).Any(e => string.IsNullOrWhiteSpace(e) || e.Length > 12))
                return "Each extension must be 1-12 characters, like .mkv.";
            return null;
        }

        private static List<Regex> Compile(List<string>? patterns)
        {
            var list = new List<Regex>();
            foreach (var p in patterns ?? [])
            {
                try { list.Add(new Regex(p, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, RegexTimeout)); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { /* skip a bad pattern */ }
            }
            return list;
        }

        public bool MatchesDistinctively(string path)
        {
            if (FilePatterns.Count == 0 && FolderPatterns.Count == 0) return false;
            var name = Path.GetFileNameWithoutExtension(path);
            if (FilePatterns.Any(r => Safe(r, name))) return true;
            var dir = Path.GetDirectoryName(path) ?? "";
            return dir.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(seg => FolderPatterns.Any(r => Safe(r, seg)));
        }

        public bool MatchesExtension(string path) => Extensions.Contains(Path.GetExtension(path));

        private static bool Safe(Regex r, string input)
        {
            try { return r.IsMatch(input); } catch (RegexMatchTimeoutException) { return false; }
        }
    }

    public sealed record MediaTypeSuggestion(int MediaTypeId, string MediaTypeName, string Reason);

    public static class MediaTypeMismatchDetector
    {
        public const double MajorityShare = 0.6;
        public const double MinorityShare = 0.2;

        /// <summary>Does this group's content look like a different type than <paramref name="current"/>? Only
        /// compares types that have hints; a type with none is never suggested and never flagged. Returns null when
        /// nothing is clearly off (including when two types fit equally well).</summary>
        public static MediaTypeSuggestion? Detect(IReadOnlyList<string> files, MediaType current, IReadOnlyList<MediaType> candidates)
        {
            if (files.Count == 0) return null;
            var currentHints = ScanHints.Parse(current.ScanHintsJson);
            if (currentHints is null) return null;

            var curDistinct = Share(files, currentHints.MatchesDistinctively);
            var curExt = Share(files, currentHints.MatchesExtension);

            MediaTypeSuggestion? best = null;
            double bestScore = 0;
            foreach (var other in candidates)
            {
                if (other.Id == current.Id || !other.IsActive) continue;
                var hints = ScanHints.Parse(other.ScanHintsJson);
                if (hints is null) continue;

                var dist = Share(files, hints.MatchesDistinctively);
                if (dist >= MajorityShare && curDistinct <= MinorityShare)
                {
                    if (dist > bestScore)
                    {
                        best = new MediaTypeSuggestion(other.Id, other.DisplayName,
                            $"{Percent(dist)} of the files look like {other.DisplayName} by name or folder, not {current.DisplayName}.");
                        bestScore = dist;
                    }
                    continue;
                }
                var ext = Share(files, hints.MatchesExtension);
                if (currentHints.Extensions.Count > 0 && ext >= MajorityShare && curExt <= MinorityShare && curDistinct <= MinorityShare && ext > bestScore)
                {
                    best = new MediaTypeSuggestion(other.Id, other.DisplayName,
                        $"{Percent(ext)} of the files are the kind {other.DisplayName} holds, and none are the kind {current.DisplayName} holds.");
                    bestScore = ext;
                }
            }
            return best;
        }

        public static void Annotate(IEnumerable<ScanGroup> groups, MediaType current, IReadOnlyList<MediaType> candidates)
        {
            foreach (var g in groups)
            {
                var suggestion = Detect(CollectFiles(g), current, candidates);
                if (suggestion is null) continue;
                g.SuggestedMediaTypeId = suggestion.MediaTypeId;
                g.SuggestedMediaTypeName = suggestion.MediaTypeName;
                g.SuggestedMediaTypeReason = suggestion.Reason;
            }
        }

        private static List<string> CollectFiles(ScanGroup g) => g.Files.Concat(g.Children.SelectMany(CollectFiles)).ToList();
        private static double Share(IReadOnlyList<string> files, Func<string, bool> test) => files.Count(test) / (double)files.Count;
        private static string Percent(double v) => $"{Math.Round(v * 100)}%";
    }
}
