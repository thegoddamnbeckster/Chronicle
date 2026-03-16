using Chronicle.Core.Models.Scan;

namespace Chronicle.Services.Scan
{
    public class ScanGroupingService : IScanGroupingService
    {
        // Extensions that are metadata/sidecar — never become MediaItems themselves
        private static readonly HashSet<string> _sidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".nfo", ".jpg", ".jpeg", ".png", ".webp", ".bmp",
            ".tbn", ".txt", ".xml", ".srt", ".sub", ".idx",
        };

        private readonly FolderSignalExtractor _folder;
        private readonly TagSignalExtractor _tags;
        private readonly NfoSignalExtractor _nfo;

        public ScanGroupingService(
            FolderSignalExtractor folder,
            TagSignalExtractor tags,
            NfoSignalExtractor nfo)
        {
            _folder = folder;
            _tags   = tags;
            _nfo    = nfo;
        }

        public ScanGroupResult Group(
            IEnumerable<string> filePaths, string scanRoot, int hierarchyLevels)
        {
            var result = new ScanGroupResult();
            // root key → ScanGroup
            var rootGroups = new Dictionary<string, ScanGroup>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in filePaths)
            {
                result.TotalFiles++;

                var ext = Path.GetExtension(path);
                bool isSidecar = _sidecarExtensions.Contains(ext);

                var folderSignal = _folder.Extract(path, scanRoot);
                var tagSignal    = _tags.Extract(path); // null for sidecars / non-audio
                var nfoPath      = _nfo.FindSidecar(path);
                var nfoSignal    = nfoPath != null ? _nfo.Extract(nfoPath) : null;

                // For flat-grouped types (audiobooks etc.), all files in the same
                // immediate folder = one item.  Sidecars are still silently absorbed.
                if (hierarchyLevels == 1)
                {
                    var groupName = folderSignal.FolderNames.LastOrDefault()
                        ?? Path.GetFileNameWithoutExtension(path);
                    var key = Normalize(groupName);

                    if (!rootGroups.TryGetValue(key, out var group))
                    {
                        group = new ScanGroup
                        {
                            GroupKey       = key,
                            Name           = groupName,
                            HierarchyLevel = 0,
                            ConfidenceScore = 0.7,
                            SignalSources  = ["folder"],
                        };
                        rootGroups[key] = group;
                        result.Groups.Add(group);
                    }

                    // Only add non-sidecar files as importable items
                    if (!isSidecar)
                        group.Files.Add(path);
                    continue;
                }

                // Hierarchical types: build Artist → Album → Track tree from folder depth
                if (folderSignal.FolderNames.Count == 0)
                {
                    // File is directly in the scan root with no folder grouping
                    if (!isSidecar)
                        result.Ungrouped.Add(path);
                    continue;
                }

                // Level 0 name: first folder name (unless overridden by tag/nfo signal)
                var level0Name = ResolveLevel0Name(folderSignal, tagSignal, nfoSignal, hierarchyLevels);
                var level0Key  = Normalize(level0Name);

                if (!rootGroups.TryGetValue(level0Key, out var rootGroup))
                {
                    rootGroup = new ScanGroup
                    {
                        GroupKey        = level0Key,
                        Name            = level0Name,
                        HierarchyLevel  = 0,
                        ConfidenceScore = ComputeRootConfidence(folderSignal, tagSignal, nfoSignal),
                        SignalSources   = BuildSources(folderSignal, tagSignal, nfoSignal, 0),
                    };
                    rootGroups[level0Key] = rootGroup;
                    result.Groups.Add(rootGroup);
                }

                // If only 1 folder deep (no album/season level), attach file directly to root
                if (hierarchyLevels == 2 || folderSignal.FolderNames.Count < 2)
                {
                    if (!isSidecar)
                    {
                        var leafName = ResolveLeafName(folderSignal, tagSignal, nfoSignal);
                        rootGroup.Children.Add(new ScanGroup
                        {
                            GroupKey        = Normalize(leafName),
                            Name            = leafName,
                            HierarchyLevel  = 1,
                            ConfidenceScore = ComputeLeafConfidence(folderSignal, tagSignal, nfoSignal),
                            SignalSources   = BuildSources(folderSignal, tagSignal, nfoSignal, 1),
                            Files           = [path],
                        });
                    }
                    continue;
                }

                // 2+ folders deep: resolve level-1 (album/season) and attach leaf under it
                var level1Name = folderSignal.FolderNames[1];
                var level1Key  = Normalize(level0Key + "/" + level1Name);

                var level1Group = rootGroup.Children
                    .FirstOrDefault(c => c.GroupKey == level1Key);

                if (level1Group is null)
                {
                    level1Group = new ScanGroup
                    {
                        GroupKey        = level1Key,
                        Name            = level1Name,
                        HierarchyLevel  = 1,
                        ConfidenceScore = 0.75,
                        SignalSources   = ["folder"],
                    };
                    rootGroup.Children.Add(level1Group);
                }

                if (!isSidecar)
                {
                    var leafName = ResolveLeafName(folderSignal, tagSignal, nfoSignal);
                    level1Group.Children.Add(new ScanGroup
                    {
                        GroupKey        = Normalize(level1Key + "/" + leafName),
                        Name            = leafName,
                        HierarchyLevel  = 2,
                        ConfidenceScore = ComputeLeafConfidence(folderSignal, tagSignal, nfoSignal),
                        SignalSources   = BuildSources(folderSignal, tagSignal, nfoSignal, 2),
                        Year            = tagSignal?.Year.HasValue == true ? (int?)tagSignal.Year.Value : null,
                        Files           = [path],
                    });
                }
            }

            // Roll up confidence scores from children to parents
            foreach (var g in result.Groups)
                RollUpConfidence(g);

            return result;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static string Normalize(string s) =>
            s.Trim().ToLowerInvariant();

        private static string ResolveLevel0Name(
            FolderSignal folder, TagSignal? tag, NfoSignal? nfo, int levels)
        {
            // Tag: prefer AlbumArtist over Artist for level-0 when music
            if (tag?.AlbumArtist is not null) return tag.AlbumArtist;
            if (nfo?.Artist is not null)      return nfo.Artist;
            if (nfo?.ShowTitle is not null)   return nfo.ShowTitle;
            return folder.FolderNames[0];
        }

        private static string ResolveLeafName(
            FolderSignal folder, TagSignal? tag, NfoSignal? nfo)
        {
            if (tag?.Title is not null) return tag.Title;
            if (nfo?.Title is not null) return nfo.Title;
            return folder.FileName;
        }

        private static double ComputeRootConfidence(
            FolderSignal folder, TagSignal? tag, NfoSignal? nfo)
        {
            double score = 0.5; // folder alone
            if (tag?.AlbumArtist is not null || tag?.Artist is not null) score += 0.25;
            if (nfo?.Artist is not null || nfo?.ShowTitle is not null)   score += 0.25;
            // Conflict: tag artist name disagrees with folder name
            var folderName = folder.FolderNames.FirstOrDefault() ?? "";
            var tagName    = tag?.AlbumArtist ?? tag?.Artist ?? "";
            if (!string.IsNullOrEmpty(tagName)
                && !folderName.Contains(tagName, StringComparison.OrdinalIgnoreCase)
                && !tagName.Contains(folderName, StringComparison.OrdinalIgnoreCase))
            {
                score -= 0.15;
            }
            return Math.Clamp(score, 0.0, 1.0);
        }

        private static double ComputeLeafConfidence(
            FolderSignal folder, TagSignal? tag, NfoSignal? nfo)
        {
            double score = 0.5;
            if (tag?.Title is not null) score += 0.25;
            if (nfo?.Title is not null) score += 0.25;
            return Math.Clamp(score, 0.0, 1.0);
        }

        private static List<string> BuildSources(
            FolderSignal folder, TagSignal? tag, NfoSignal? nfo, int level)
        {
            var sources = new List<string> { "folder" };
            if (tag is not null) sources.Add("tags");
            if (nfo is not null) sources.Add("nfo");
            return sources;
        }

        private static void RollUpConfidence(ScanGroup group)
        {
            if (group.Children.Count == 0) return;
            foreach (var child in group.Children) RollUpConfidence(child);
            group.ConfidenceScore = group.Children.Average(c => c.ConfidenceScore);
        }
    }
}
