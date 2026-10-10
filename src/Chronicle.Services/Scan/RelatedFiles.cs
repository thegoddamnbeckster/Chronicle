using Chronicle.Core.Models;
using Chronicle.Core.Models.Scan;

namespace Chronicle.Services.Scan
{
    /// <summary>What counts as a supplemental file, and what kind. The lists are the defaults; an administrator can
    /// replace either through <c>scan.sidecar_extensions</c> / <c>scan.sidecar_folders</c> in app_settings (comma
    /// separated), so a new subtitle format or extras folder name never needs a release.</summary>
    public sealed class SidecarRules
    {
        public const string ExtensionsKey = "scan.sidecar_extensions";
        public const string FoldersKey = "scan.sidecar_folders";

        public static readonly string[] DefaultExtensions =
        [
            ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tbn", ".txt", ".xml", ".srt", ".sub", ".idx", ".ass", ".cue", ".log",
        ];

        public static readonly string[] DefaultFolders =
        [
            "theme-music", "theme music", ".theme", ".actors", "extrafanart", "extrathumbs", "behind the scenes", "behindthescenes",
            "deleted scenes", "deletedscenes", "featurettes", "interviews", "scenes", "shorts", "trailers", "extras",
        ];

        private static readonly HashSet<string> SubtitleExt = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".sub", ".idx", ".ass", ".ssa", ".vtt" };
        private static readonly HashSet<string> ArtExt = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tbn" };

        public HashSet<string> Extensions { get; }
        public HashSet<string> Folders { get; }

        public SidecarRules(IEnumerable<string> extensions, IEnumerable<string> folders)
        {
            Extensions = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
            Folders = new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase);
        }

        public static SidecarRules Defaults { get; } = new(DefaultExtensions, DefaultFolders);

        /// <summary>The configured lists, falling back to the defaults for a missing or empty row. Extensions are
        /// normalised to start with a dot.</summary>
        public static SidecarRules From(IReadOnlyDictionary<string, string>? settings)
        {
            string[] Split(string key, string[] fallback)
            {
                if (settings is null || !settings.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return fallback;
                var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                return parts.Length == 0 ? fallback : parts;
            }
            var ext = Split(ExtensionsKey, DefaultExtensions).Select(e => e.StartsWith('.') ? e : "." + e);
            return new SidecarRules(ext, Split(FoldersKey, DefaultFolders));
        }

        /// <summary>subtitle / artwork / theme / extra / booklet / other, from the file's extension and the folders above it.</summary>
        public string Classify(string path, IEnumerable<string> folderNames)
        {
            var ext = Path.GetExtension(path);
            var folders = folderNames.Select(f => f.ToLowerInvariant()).ToList();
            if (folders.Any(f => f.Contains("theme"))) return RelatedFileKinds.Theme;
            if (SubtitleExt.Contains(ext)) return RelatedFileKinds.Subtitle;
            if (folders.Any(f => Folders.Contains(f) && !f.Contains("fanart") && !f.Contains("thumbs") && f != ".actors")) return RelatedFileKinds.Extra;
            if (ArtExt.Contains(ext) || folders.Any(f => f.Contains("fanart") || f.Contains("thumbs") || f == ".actors")) return RelatedFileKinds.Artwork;
            if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cue", StringComparison.OrdinalIgnoreCase)) return RelatedFileKinds.Booklet;
            return RelatedFileKinds.Other;
        }
    }

    public sealed record ScanGroupOptions(bool CollectRelatedFiles = false, IReadOnlyList<MediaType>? MismatchCandidates = null, MediaType? ScannedType = null);

    internal static class RelatedFileAttacher
    {
        private static string Key(string path) =>
            path.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();

        /// <summary>Gives each related file to the most specific group it sits under (deepest folder match, found by
        /// walking up from the file's own folder, so cost does not grow with the number of groups); a loose file with no
        /// owning folder is matched to the loose media file with the same name. Unmatched files are dropped.</summary>
        public static void Attach(IEnumerable<ScanGroup> roots, IEnumerable<string> relatedPaths)
        {
            var all = new List<ScanGroup>();
            void Walk(ScanGroup g) { all.Add(g); foreach (var c in g.Children) Walk(c); }
            foreach (var r in roots) Walk(r);

            // Folder -> owning group. If two groups claim one folder (a real "Season 1" and a synthesised one), the
            // deeper level in the tree wins.
            var byFolder = new Dictionary<string, ScanGroup>();
            foreach (var g in all.Where(g => !string.IsNullOrEmpty(g.FolderPath)))
            {
                var key = Key(g.FolderPath!);
                if (!byFolder.TryGetValue(key, out var existing) || g.HierarchyLevel > existing.HierarchyLevel)
                    byFolder[key] = g;
            }
            var looseLeaves = all.Where(g => string.IsNullOrEmpty(g.FolderPath) && g.Files.Count == 1)
                .GroupBy(g => Key(Path.GetDirectoryName(g.Files[0]) ?? ""))
                .ToDictionary(x => x.Key, x => x.ToList());

            foreach (var rel in relatedPaths)
            {
                var dir = Path.GetDirectoryName(rel.Replace('/', Path.DirectorySeparatorChar));
                ScanGroup? owner = null;
                for (var d = dir; !string.IsNullOrEmpty(d) && owner is null; d = Path.GetDirectoryName(d))
                    byFolder.TryGetValue(Key(d), out owner);

                if (owner is null && dir is not null && looseLeaves.TryGetValue(Key(dir), out var candidates))
                {
                    var stem = Path.GetFileNameWithoutExtension(rel);
                    owner = candidates.FirstOrDefault(g =>
                        stem.StartsWith(Path.GetFileNameWithoutExtension(g.Files[0]), StringComparison.OrdinalIgnoreCase));
                }
                owner?.RelatedFiles.Add(rel);
            }
        }
    }
}
