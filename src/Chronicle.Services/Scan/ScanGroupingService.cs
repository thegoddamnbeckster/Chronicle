using System.Text.RegularExpressions;
using Chronicle.Core.Models.Scan;

namespace Chronicle.Services.Scan
{
    public class ScanGroupingService : IScanGroupingService
    {
        // Compiled regex constants used during grouping
        private static readonly Regex _yearSuffixRe  = new(@"\s*\((\d{4})\)\s*$",           RegexOptions.Compiled);
        private static readonly Regex _yearPresentRe = new(@"\(\d{4}\)",                    RegexOptions.Compiled);
        private static readonly Regex _seasonNumRe   = new(@"(?:Season|S)\s*0*(\d+)",        RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Extensions that are metadata/sidecar — never become MediaItems themselves
        private static readonly HashSet<string> _sidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".nfo", ".jpg", ".jpeg", ".png", ".webp", ".bmp",
            ".tbn", ".txt", ".xml", ".srt", ".sub", ".idx",
        };

        // Folder names whose entire contents are treated as sidecar/supplemental material.
        // Any file inside one of these folders is excluded from grouping (same as a sidecar file),
        // regardless of its extension (e.g. theme-music .mp3, .actors images, extras .mkv, etc.).
        private static readonly HashSet<string> _sidecarFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "theme-music", "theme music", ".theme",
            ".actors",
            "extrafanart", "extrathumbs",
            "behind the scenes", "behindthescenes",
            "deleted scenes", "deletedscenes",
            "featurettes",
            "interviews",
            "scenes",
            "shorts",
            "trailers",
            "extras",
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

                // Treat any file inside a known supplemental folder as a sidecar,
                // regardless of its extension (e.g. theme-music/*.mp3, .actors/*.jpg).
                var folderSignal = _folder.Extract(path, scanRoot);
                if (!isSidecar && folderSignal.FolderNames.Any(f => _sidecarFolderNames.Contains(f)))
                    isSidecar = true;
                var tagSignal    = _tags.Extract(path); // null for sidecars / non-audio
                var nfoPath      = _nfo.FindSidecar(path);
                var nfoSignal    = nfoPath != null ? _nfo.Extract(nfoPath) : null;

                // For flat-grouped types (movies etc.), all files in the same
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
                            GroupKey        = key,
                            Name            = groupName,
                            HierarchyLevel  = 0,
                            ConfidenceScore = ComputeFlatConfidence(groupName, nfoSignal),
                            SignalSources   = BuildSources(folderSignal, null, nfoSignal, 0),
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

                // Extract a trailing "(YYYY)" year from the resolved name, then strip it so
                // "Home Town (2016)" and "Home Town" share the same group key.
                var yearMatch   = _yearSuffixRe.Match(level0Name);
                var level0Clean = yearMatch.Success ? level0Name[..yearMatch.Index].TrimEnd() : level0Name;
                var level0Key   = Normalize(level0Clean);

                // Prefer the year embedded in the resolved name; if tags/nfo produced the name
                // without a year suffix (e.g. tags say "Enterprise" but folder says
                // "Star Trek, Enterprise (2001)"), fall back to extracting it from the raw
                // folder name on disk.
                int? level0Year;
                if (yearMatch.Success)
                {
                    level0Year = int.Parse(yearMatch.Groups[1].Value);
                }
                else
                {
                    var folderYearMatch = _yearSuffixRe.Match(folderSignal.FolderNames[0]);
                    level0Year = folderYearMatch.Success
                        ? int.Parse(folderYearMatch.Groups[1].Value)
                        : (int?)null;
                }

                if (!rootGroups.TryGetValue(level0Key, out var rootGroup))
                {
                    rootGroup = new ScanGroup
                    {
                        GroupKey        = level0Key,
                        Name            = level0Clean,
                        Year            = level0Year,
                        HierarchyLevel  = 0,
                        ConfidenceScore = ComputeRootConfidence(folderSignal, tagSignal, nfoSignal),
                        SignalSources   = BuildSources(folderSignal, tagSignal, nfoSignal, 0),
                        FolderPath      = Path.Combine(scanRoot, folderSignal.FolderNames[0]),
                    };
                    rootGroups[level0Key] = rootGroup;
                    result.Groups.Add(rootGroup);
                }
                else if (level0Year.HasValue && !rootGroup.Year.HasValue)
                {
                    // Propagate year to existing group if we just found it
                    rootGroup.Year = level0Year;
                }

                // If only 1 folder deep (no album/season level), attach file to root —
                // but for 3-level types with S##E## in the filename, synthesise a Season
                // group so the episode lands at the correct depth (level 2, not level 1).
                if (hierarchyLevels == 2 || folderSignal.FolderNames.Count < 2)
                {
                    if (!isSidecar)
                    {
                        var leafName   = ResolveLeafName(folderSignal, tagSignal, nfoSignal);
                        var leafNumber = ResolveLeafNumber(folderSignal, tagSignal);

                        if (hierarchyLevels >= 3 && folderSignal.DetectedEpisode.HasValue)
                        {
                            // Synthesise a Season group from the detected season number so the
                            // episode is at depth 2 during import (Episode), not depth 1 (Season).
                            var seasonNum  = folderSignal.DetectedSeason ?? 1;
                            var seasonName = seasonNum == 0 ? "Specials" : $"Season {seasonNum}";
                            var seasonKey  = Normalize(level0Key + "/" + seasonName);

                            var seasonGroup = rootGroup.Children
                                .FirstOrDefault(c => c.GroupKey == seasonKey);
                            if (seasonGroup is null)
                            {
                                seasonGroup = new ScanGroup
                                {
                                    GroupKey        = seasonKey,
                                    Name            = seasonName,
                                    Number          = seasonNum,
                                    HierarchyLevel  = 1,
                                    ConfidenceScore = 0.85,
                                    SignalSources   = ["filename"],
                                };
                                rootGroup.Children.Add(seasonGroup);
                            }

                            seasonGroup.Children.Add(new ScanGroup
                            {
                                GroupKey        = Normalize(seasonKey + "/" + leafName),
                                Name            = leafName,
                                Number          = leafNumber,
                                HierarchyLevel  = 2,
                                ConfidenceScore = ComputeLeafConfidence(folderSignal, tagSignal, nfoSignal),
                                SignalSources   = BuildSources(folderSignal, tagSignal, nfoSignal, 2),
                                Files           = [path],
                            });
                        }
                        else if (hierarchyLevels < 3)
                        {
                            // 2-level type (e.g. audiobook chapter, movie file): attach directly to root.
                            rootGroup.Children.Add(new ScanGroup
                            {
                                GroupKey        = Normalize(leafName),
                                Name            = leafName,
                                Number          = leafNumber,
                                HierarchyLevel  = 1,
                                ConfidenceScore = ComputeLeafConfidence(folderSignal, tagSignal, nfoSignal),
                                SignalSources   = BuildSources(folderSignal, tagSignal, nfoSignal, 1),
                                Files           = [path],
                            });
                        }
                        // else: 3-level type (TV/music), file is directly in the root folder with no
                        // episode/track pattern detected — treat as supplemental and skip.
                        // This prevents theme.mp3, stray images, etc. from becoming spurious Season nodes.
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
                        Number          = ResolveLevel1Number(level1Name, folderSignal),
                        HierarchyLevel  = 1,
                        ConfidenceScore = 0.75,
                        SignalSources   = ["folder"],
                        FolderPath      = Path.Combine(scanRoot, folderSignal.FolderNames[0], folderSignal.FolderNames[1]),
                    };
                    rootGroup.Children.Add(level1Group);
                }

                if (!isSidecar)
                {
                    var leafName   = ResolveLeafName(folderSignal, tagSignal, nfoSignal);
                    var leafNumber = ResolveLeafNumber(folderSignal, tagSignal);
                    level1Group.Children.Add(new ScanGroup
                    {
                        GroupKey        = Normalize(level1Key + "/" + leafName),
                        Name            = leafName,
                        Number          = leafNumber,
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

            // Prune empty nodes: remove children with no media files at any level
            foreach (var g in result.Groups)
                PruneEmptyChildren(g);

            // Remove root groups that ended up with no files at all (sidecar-only folders)
            result.Groups.RemoveAll(g => g.TotalFileCount == 0);

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

        /// <summary>
        /// Returns the episode/track number for a leaf item.
        /// Priority: tag TrackNumber → folder-detected episode → folder-detected track number.
        /// </summary>
        private static int? ResolveLeafNumber(FolderSignal folder, TagSignal? tag)
        {
            if (tag?.TrackNumber.HasValue == true)
                return (int)tag.TrackNumber.Value;
            if (folder.DetectedEpisode.HasValue)
                return folder.DetectedEpisode;
            if (folder.DetectedTrackNumber.HasValue)
                return folder.DetectedTrackNumber;
            return null;
        }

        /// <summary>
        /// Returns the season/disc number for a level-1 group (folder-based).
        /// Tries the folder name first, then the folder signal's DetectedSeason.
        /// </summary>
        private static int? ResolveLevel1Number(string level1Name, FolderSignal folder)
        {
            var m = _seasonNumRe.Match(level1Name);
            if (m.Success)
                return int.Parse(m.Groups[1].Value);

            if (folder.DetectedSeason.HasValue)
                return folder.DetectedSeason;
            if (folder.DetectedDiscNumber.HasValue)
                return folder.DetectedDiscNumber;
            return null;
        }

        private static double ComputeRootConfidence(
            FolderSignal folder, TagSignal? tag, NfoSignal? nfo)
        {
            double score = 0.55; // folder name alone
            if (tag?.AlbumArtist is not null || tag?.Artist is not null) score += 0.20;
            if (nfo?.Artist is not null || nfo?.ShowTitle is not null)   score += 0.20;
            // Year in folder name is a meaningful signal even without tags/NFO
            var folderName = folder.FolderNames.FirstOrDefault() ?? "";
            if (_yearPresentRe.IsMatch(folderName))
                score += 0.20;
            // Conflict: tag artist name disagrees with folder name
            var tagName = tag?.AlbumArtist ?? tag?.Artist ?? "";
            if (!string.IsNullOrEmpty(tagName)
                && !folderName.Contains(tagName, StringComparison.OrdinalIgnoreCase)
                && !tagName.Contains(folderName, StringComparison.OrdinalIgnoreCase))
            {
                score -= 0.15;
            }
            return Math.Clamp(score, 0.0, 1.0);
        }

        /// <summary>
        /// Computes confidence for flat (hierarchyLevels == 1) groups such as movies.
        /// Scores reflect signal quality honestly — the user-configurable threshold
        /// determines what gets auto-imported; these values should not be chosen to
        /// artificially pass any particular threshold.
        /// </summary>
        private static double ComputeFlatConfidence(string groupName, NfoSignal? nfo)
        {
            if (nfo?.ExternalId is not null)              return 1.00; // NFO has exact external ID
            if (nfo?.Title is not null && nfo.Year.HasValue) return 0.90; // NFO title + year
            if (nfo?.Title is not null)                   return 0.78; // NFO title only
            // "(YYYY)" in folder name: reliable naming convention used by most media managers
            if (_yearPresentRe.IsMatch(groupName))
                return 0.75;
            // Folder name only — title is plausible but year is unknown
            return 0.55;
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

        /// <summary>
        /// Recursively removes children (at any depth) that contain no media files.
        /// This eliminates sidecar-only directories like .actors, theme-music, etc.
        /// from the import preview.
        /// </summary>
        private static void PruneEmptyChildren(ScanGroup group)
        {
            foreach (var child in group.Children)
                PruneEmptyChildren(child);

            group.Children.RemoveAll(c => c.TotalFileCount == 0);
        }
    }
}
