using System.Text.RegularExpressions;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models.Scan;
using Chronicle.Plugins.Models;
using Chronicle.Services.Plugins;

namespace Chronicle.Services.Scan
{
    public class ScanGroupingService : IScanGroupingService
    {
        // Compiled regex constants used during grouping
        private static readonly Regex _yearSuffixRe  = new(@"\s*\((\d{4})\)\s*$",           RegexOptions.Compiled);
        private static readonly Regex _yearPresentRe = new(@"\(\d{4}\)",                    RegexOptions.Compiled);
        private static readonly Regex _seasonNumRe   = new(@"(?:Season|S)\s*0*(\d+)",        RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly FolderSignalExtractor _folder;
        private readonly TagSignalExtractor _tags;

        private readonly Chronicle.Services.Security.ICachedAppSettings? _settings;

        public ScanGroupingService(
            FolderSignalExtractor folder,
            TagSignalExtractor tags,
            Chronicle.Services.Security.ICachedAppSettings? settings = null)
        {
            _folder   = folder;
            _tags     = tags;
            _settings = settings;
        }

        /// <summary>
        /// Per-file results of the expensive, I/O-bound work (tag reads)
        /// that <see cref="Group"/> needs before it can build the group tree. Computed in
        /// parallel across files ahead of time, since each file's extraction is independent of
        /// every other file's -- unlike the tree-building loop itself, which mutates a shared
        /// dictionary/list structure and must stay sequential.
        /// </summary>
        private sealed record PerFileSignals(
            bool IsJunk,
            bool IsSidecar,
            FolderSignal Folder,
            TagSignal? Tag);

        public ScanGroupResult Group(
            IEnumerable<string> filePaths, string scanRoot, int hierarchyLevels, ScanGroupOptions? options = null)
        {
            var result = new ScanGroupResult();
            // Which extensions / folder names count as supplemental: app_settings, falling back to the defaults.
            var rules = SidecarRules.From(_settings?.Snapshot);
            var mediaRules = MediaFileRules.From(_settings?.Snapshot);   // built-in media extensions plus the administrator's own
            var related = new List<string>();
            // root key → ScanGroup
            var rootGroups = new Dictionary<string, ScanGroup>(StringComparer.OrdinalIgnoreCase);

            var pathList = filePaths as IReadOnlyList<string> ?? filePaths.ToList();
            var signals  = new PerFileSignals[pathList.Count];

            // Parallel pass: tag extraction is a pure, per-file disk read
            // reads with no shared state, and dominate scan time on large libraries (thousands
            // of files scanned one at a time was the actual bottleneck, not directory listing).
            System.Threading.Tasks.Parallel.For(0, pathList.Count, i =>
            {
                var path = pathList[i];
                var ext = Path.GetExtension(path);
                bool isSidecar = rules.Extensions.Contains(ext);

                // Treat any file inside a known supplemental folder as a sidecar,
                // regardless of its extension (e.g. theme-music/*.mp3, .actors/*.jpg).
                var folderSignal = _folder.Extract(path, scanRoot, mediaRules.Audio);
                if (!isSidecar && folderSignal.FolderNames.Any(f => rules.Folders.Contains(f)))
                    isSidecar = true;

                // Anything that's neither a recognized sidecar NOR a recognized playable media
                // extension is junk as far as importing goes (cache files, lock files, Kodi's own
                // ".metathumb" thumbnail cache, etc.) -- skip it entirely rather than defaulting
                // to "not a sidecar, so it must be media" (confirmed bug 2026-08-29: a stray
                // ".metathumb" file sitting next to a real .mp4 got imported as the movie's own
                // file, and picked ahead of the real file whenever paths were sorted/read back).
                bool isJunk = !isSidecar && !mediaRules.Recognized.Contains(ext);

                // Skip expensive tag extraction for files we've already classified as
                // sidecars or junk.
                TagSignal? tagSignal = null;
                if (!isSidecar && !isJunk)
                    tagSignal = _tags.Extract(path);

                signals[i] = new PerFileSignals(isJunk, isSidecar, folderSignal, tagSignal);
            });

            DistrustLoneDiscTrackNames(pathList, signals);

            // Sequential pass: build the group tree in original file order using the
            // precomputed signals -- identical logic/output to before, just no longer doing
            // the disk I/O itself.
            for (int fi = 0; fi < pathList.Count; fi++)
            {
                var path = pathList[fi];
                var sig  = signals[fi];
                result.TotalFiles++;

                if (sig.IsJunk)
                    continue;

                if (sig.IsSidecar && options?.CollectRelatedFiles == true)
                    related.Add(path);

                bool isSidecar = sig.IsSidecar;
                var folderSignal = sig.Folder;
                var tagSignal = sig.Tag;

                // For flat-grouped types (movies etc.), all files in the same
                // immediate folder = one item.  Sidecars are still silently absorbed.
                if (hierarchyLevels == 1)
                {
                    var groupName = folderSignal.FolderNames.LastOrDefault()
                        ?? Path.GetFileNameWithoutExtension(path);

                    // A download-style name ("Movie.Name.2019.1080p.BluRay.x264-GRP") becomes "Movie Name (2019)".
                    // Tidy names such as "Heat (1995)" carry no quality words and are left exactly as they are.
                    var release = ReleaseNameParser.Parse(groupName);
                    bool derivedName = release.LooksLikeRelease && release.Title is not null;
                    if (derivedName)
                        groupName = release.Year is { } releaseYear ? $"{release.Title} ({releaseYear})" : release.Title!;
                    var key = Normalize(groupName);

                    if (!rootGroups.TryGetValue(key, out var group))
                    {
                        // Set FolderPath to the containing directory so UpsertGroupItemAsync
                        // can match this scan group against an existing DB item by folder path.
                        // This survives enrichment renaming the item (e.g. "The Matrix Revolutions
                        // Decoded" → "The Matrix Revolutions") because the folder on disk doesn't change.
                        // Only set it when the file lives in a subfolder of the scan root; files
                        // sitting directly in the root share a directory and would collide.
                        var folderPath = folderSignal.FolderNames.Count > 0
                            ? Path.GetDirectoryName(path)
                            : null;

                        group = new ScanGroup
                        {
                            GroupKey        = key,
                            Name            = groupName,
                            // The year in "Title (Year)". Without it the importer cannot tell "The Exorcist (1973)" from
                            // "The Exorcist (2023)" and the second scan overwrites the first one's file path.
                            Year            = FolderNameYear.Split(groupName).Year,
                            HierarchyLevel  = 0,
                            // A name worked out from a release string is less certain than a tidy folder name.
                            ConfidenceScore = derivedName ? Math.Min(0.7, ComputeFlatConfidence(groupName)) : ComputeFlatConfidence(groupName),
                            SignalSources   = BuildSources(folderSignal, null, 0),
                            FolderPath      = folderPath,
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
                    // File is directly in the scan root with no folder grouping. A TV-style episode name
                    // ("Show.Name.S02E03...") still tells us the show, season and episode.
                    if (!isSidecar && hierarchyLevels >= 3 && !mediaRules.Audio.Contains(Path.GetExtension(path))
                        && TryAddEpisodeFromFileName(rootGroups, result, path, scanRoot, folderSignal, tagSignal))
                        continue;
                    if (!isSidecar)
                        result.Ungrouped.Add(path);
                    continue;
                }

                // Level 0 name: first folder name (unless overridden by tag signal)
                var level0Name = ResolveLevel0Name(folderSignal, tagSignal, hierarchyLevels);

                // Extract a trailing "(YYYY)" year from the resolved name, then strip it so
                // "Home Town (2016)" and "Home Town" share the same group key.
                var yearMatch   = _yearSuffixRe.Match(level0Name);
                var level0Clean = yearMatch.Success ? level0Name[..yearMatch.Index].TrimEnd() : level0Name;
                var level0Key   = Normalize(level0Clean);

                // Prefer the year embedded in the resolved name; if tags produced the name
                // without a year suffix (e.g. tags say "Enterprise" but folder says
                // "Star Trek, Enterprise (2001)"), fall back to extracting it from the raw
                // folder name on disk.
                int? level0Year;
                if (yearMatch.Success && DigitParsingHelper.TryParseDigits(yearMatch.Groups[1].Value, out var parsedLevel0Year))
                {
                    level0Year = parsedLevel0Year;
                }
                else
                {
                    var folderYearMatch = _yearSuffixRe.Match(folderSignal.FolderNames[0]);
                    level0Year = folderYearMatch.Success
                        && DigitParsingHelper.TryParseDigits(folderYearMatch.Groups[1].Value, out var parsedFolderYear)
                        ? parsedFolderYear
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
                        ConfidenceScore = ComputeRootConfidence(folderSignal, tagSignal),
                        SignalSources   = BuildSources(folderSignal, tagSignal, 0),
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
                        var leafName   = ResolveLeafName(folderSignal, tagSignal);
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
                                    // No real "Season N" subfolder exists on disk here (that's exactly
                                    // why this group is being synthesized), but FileScanService's
                                    // UpsertGroupItemAsync still uses FolderPath purely as a matching
                                    // key, not a filesystem access — giving this synthesized group a
                                    // unique, show-scoped path lets it match via the strong secondary
                                    // (folder-path) tier on re-scan instead of falling through to the
                                    // unscoped-by-default tertiary name-only tier, which is what let an
                                    // entire season get misfiled under an unrelated show's identically-
                                    // numbered season (confirmed 2026-08-05; the tertiary tier itself was
                                    // separately fixed to be ParentId/HierarchyLevel-scoped, but this closes
                                    // the same hole one layer earlier, in depth).
                                    FolderPath      = Path.Combine(scanRoot, folderSignal.FolderNames[0], seasonName),
                                };
                                rootGroup.Children.Add(seasonGroup);
                            }

                            seasonGroup.Children.Add(new ScanGroup
                            {
                                GroupKey        = Normalize(seasonKey + "/" + leafName),
                                Name            = leafName,
                                Number          = leafNumber,
                                HierarchyLevel  = 2,
                                ConfidenceScore = ComputeLeafConfidence(folderSignal, tagSignal),
                                SignalSources   = BuildSources(folderSignal, tagSignal, 2),
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
                                ConfidenceScore = ComputeLeafConfidence(folderSignal, tagSignal),
                                SignalSources   = BuildSources(folderSignal, tagSignal, 1),
                                Files           = [path],
                            });
                        }
                        else if (hierarchyLevels >= 3 && !mediaRules.Audio.Contains(Path.GetExtension(path))
                                 && ReleaseNameParser.Parse(folderSignal.FileName) is { HasEpisodeNumbering: true } named)
                        {
                            // No S01E02 code, but the name still numbers the episode: a running number ("Show - 112",
                            // anime style) or an air date. The show is the folder the file sits in.
                            AttachNamedEpisode(rootGroup, level0Key, folderSignal.FolderNames[0], named, path, scanRoot, folderSignal, tagSignal);
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
                    // A non-season level-1 folder is an album: "(2000) The Better Life" is the name "The Better
                    // Life" released in 2000. The year goes in the Year field, never in the name.
                    var level1Resolved = ResolveLevel1Name(level1Name, folderSignal);
                    var (level1Clean, level1Year) = level1Resolved == level1Name
                        ? FolderNameYear.Split(level1Name)
                        : (level1Resolved, (int?)null);
                    level1Group = new ScanGroup
                    {
                        GroupKey        = level1Key,
                        Name            = level1Clean,
                        Year            = level1Year,
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
                    var leafName   = ResolveLeafName(folderSignal, tagSignal);
                    var leafNumber = ResolveLeafNumber(folderSignal, tagSignal);
                    level1Group.Children.Add(new ScanGroup
                    {
                        GroupKey        = Normalize(level1Key + "/" + leafName),
                        Name            = leafName,
                        Number          = leafNumber,
                        HierarchyLevel  = 2,
                        ConfidenceScore = ComputeLeafConfidence(folderSignal, tagSignal),
                        SignalSources   = BuildSources(folderSignal, tagSignal, 2),
                        Year            = tagSignal?.Year.HasValue == true ? (int?)tagSignal.Year.Value : null,
                        Files           = [path],
                    });
                }
            }

            // Prune empty nodes FIRST: remove children with no media files at any level
            // (e.g. a leftover "Season 1" folder holding only orphaned .xml/.jpg sidecars
            // next to the real "Season 01" folder that has the actual video files). This
            // MUST happen before the confidence roll-up below -- an empty phantom child
            // was previously still averaged into its parent's confidence score even though
            // it gets deleted from the tree a moment later, silently dragging a
            // well-formed show's score below the auto-import threshold. Confirmed
            // 2026-08-24: a show with one correct season folder and one leftover empty
            // one scored 78% instead of 85%, just missing the default 80% cutoff.
            foreach (var g in result.Groups)
                PruneEmptyChildren(g);

            // Roll up confidence scores from children to parents, now that only real,
            // file-bearing groups remain in the tree.
            foreach (var g in result.Groups)
                RollUpConfidence(g);

            // Remove root groups that ended up with no files at all (sidecar-only folders)
            result.Groups.RemoveAll(g => g.TotalFileCount == 0);

            if (related.Count > 0)
                RelatedFileAttacher.Attach(result.Groups, related);

            if (options?.ScannedType is { } scanned && options.MismatchCandidates is { Count: > 0 } candidates)
                MediaTypeMismatchDetector.Annotate(result.Groups, scanned, candidates);

            return result;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        /// <summary>
        /// For an episode sitting loose in the scan root: reads the show and the episode out of its file name and files it
        /// under a show group built from that name. Returns false when the name does not place the file in a series, so the
        /// caller leaves it ungrouped as before. The show group is capped below the automatic-import threshold: there is
        /// no folder to confirm the name, so a person should look at it first.
        /// </summary>
        private static bool TryAddEpisodeFromFileName(
            Dictionary<string, ScanGroup> rootGroups, ScanGroupResult result, string path, string scanRoot,
            FolderSignal folderSignal, TagSignal? tagSignal)
        {
            var info = ReleaseNameParser.Parse(folderSignal.FileName);
            if (!info.HasEpisodeNumbering) return false;

            var showKey = Normalize(info.Title!);
            if (!rootGroups.TryGetValue(showKey, out var show))
            {
                show = new ScanGroup
                {
                    GroupKey        = showKey,
                    Name            = info.Title!,
                    Year            = info.Year,
                    HierarchyLevel  = 0,
                    ConfidenceScore = 0.6,
                    ConfidenceCap   = 0.7,
                    SignalSources   = ["filename"],
                };
                rootGroups[showKey] = show;
                result.Groups.Add(show);
            }
            else
            {
                // The show also has a real folder: keep it, and take the year if it lacked one.
                show.Year ??= info.Year;
            }

            AttachNamedEpisode(show, showKey, info.Title!, info, path, scanRoot, folderSignal, tagSignal);
            return true;
        }

        /// <summary>
        /// Files an episode whose name carries its own numbering (S02E03, a running number, or an air date) under a show
        /// group, creating the season group it belongs to. Seasons: the number in the name; "season 1" for a running number
        /// (anime-style numbering that continues across seasons: the number is kept as the episode number); the year for a
        /// daily show named by date (the date is the episode name).
        /// </summary>
        private static void AttachNamedEpisode(
            ScanGroup show, string showKey, string showFolderName, ReleaseNameInfo info, string path, string scanRoot,
            FolderSignal folderSignal, TagSignal? tagSignal)
        {
            int seasonNum;
            int? number;
            string episodeName;
            string source = "filename";
            if (info.Season is not null && info.Episode is not null)
            {
                seasonNum = info.Season.Value;
                number = info.Episode;
                episodeName = info.EpisodeTitle ?? $"Episode {info.Episode}";
            }
            else if (info.AbsoluteEpisode is not null)
            {
                seasonNum = 1;
                number = info.AbsoluteEpisode;
                episodeName = info.EpisodeTitle ?? $"Episode {info.AbsoluteEpisode}";
                source = "absolute-number";
            }
            else
            {
                seasonNum = info.AirDate!.Value.Year;
                number = null;
                episodeName = info.AirDate!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                source = "air-date";
            }

            var seasonName = seasonNum == 0 ? "Specials" : $"Season {seasonNum}";
            var seasonKey  = Normalize(showKey + "/" + seasonName);
            var season = show.Children.FirstOrDefault(c => c.GroupKey == seasonKey);
            if (season is null)
            {
                season = new ScanGroup
                {
                    GroupKey        = seasonKey,
                    Name            = seasonName,
                    Number          = seasonNum,
                    HierarchyLevel  = 1,
                    ConfidenceScore = 0.85,
                    SignalSources   = ["filename"],
                    // Unique, show-scoped path used only as a matching key (the main loop does the same for a season it invents).
                    FolderPath      = Path.Combine(scanRoot, showFolderName, seasonName),
                };
                show.Children.Add(season);
            }

            season.Children.Add(new ScanGroup
            {
                GroupKey        = Normalize(seasonKey + "/" + episodeName + "/" + (number?.ToString() ?? "")),
                Name            = episodeName,
                Number          = number,
                HierarchyLevel  = 2,
                ConfidenceScore = source == "filename" ? ComputeLeafConfidence(folderSignal, tagSignal) : 0.7,
                SignalSources   = [source],
                Files           = [path],
            });
        }

        /// <summary>"1-02 Title" names mean disc 1, track 2 only when the folder's other tracks are named that way
        /// too. A lone one ("7-11 Store.mp3") goes back to the plain reading: no disc, the file name left as it is.</summary>
        private static void DistrustLoneDiscTrackNames(IReadOnlyList<string> paths, PerFileSignals[] signals)
        {
            var perFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < paths.Count; i++)
            {
                if (!signals[i].Folder.DiscFromFileName) continue;
                var dir = Path.GetDirectoryName(paths[i]) ?? "";
                perFolder[dir] = perFolder.GetValueOrDefault(dir) + 1;
            }
            for (int i = 0; i < paths.Count; i++)
            {
                var f = signals[i].Folder;
                if (!f.DiscFromFileName || perFolder[Path.GetDirectoryName(paths[i]) ?? ""] >= 2) continue;
                f.DetectedDiscNumber = null;
                f.DiscFromFileName = false;
                f.DetectedTrackNumber = f.PlainTrackNumber;
                f.TrackTitle = null;
            }
        }

        private static string Normalize(string s) =>
            s.Trim().ToLowerInvariant();

        private static string ResolveLevel0Name(
            FolderSignal folder, TagSignal? tag, int levels)
        {
            // Tag: prefer AlbumArtist over Artist for level-0 when music
            if (tag?.AlbumArtist is not null) return tag.AlbumArtist;

            // A release-named top folder ("Show.Name.S02.1080p.BluRay.x264-GRP") is cleaned to the show name; ordinary
            // folder names are used as they are.
            var top = folder.FolderNames[0];
            var release = ReleaseNameParser.Parse(top);
            if (release.LooksLikeRelease && release.Title is not null)
                return release.Year is { } y ? $"{release.Title} ({y})" : release.Title;
            return top;
        }

        private static string ResolveLeafName(
            FolderSignal folder, TagSignal? tag)
        {
            if (tag?.Title is not null) return tag.Title;
            return folder.TrackTitle ?? folder.FileName;
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
        /// Normalizes a real on-disk level-1 folder name for display: "Season 04", "season4",
        /// "S4" all become "Season 4" (and season 0 becomes "Specials"), matching the synthesized
        /// season branch above. Non-season folders (e.g. music albums) pass through unchanged.
        /// </summary>
        private static string ResolveLevel1Name(string level1Name, FolderSignal folder)
        {
            var m = _seasonNumRe.Match(level1Name);
            if (m.Success && DigitParsingHelper.TryParseDigits(m.Groups[1].Value, out var seasonNum))
                return seasonNum == 0 ? "Specials" : $"Season {seasonNum}";
            return level1Name;
        }

        /// <summary>
        /// Returns the season/disc number for a level-1 group (folder-based).
        /// Tries the folder name first, then the folder signal's DetectedSeason.
        /// </summary>
        private static int? ResolveLevel1Number(string level1Name, FolderSignal folder)
        {
            var m = _seasonNumRe.Match(level1Name);
            if (m.Success && DigitParsingHelper.TryParseDigits(m.Groups[1].Value, out var seasonNum))
                return seasonNum;

            if (folder.DetectedSeason.HasValue)
                return folder.DetectedSeason;
            if (folder.DetectedDiscNumber.HasValue)
                return folder.DetectedDiscNumber;
            return null;
        }

        private static double ComputeRootConfidence(
            FolderSignal folder, TagSignal? tag)
        {
            double score = 0.55; // folder name alone
            if (tag?.AlbumArtist is not null || tag?.Artist is not null) score += 0.20;
            // Year in folder name is a meaningful signal even without tags
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
        private static double ComputeFlatConfidence(string groupName)
        {
            // "(YYYY)" in folder name: reliable naming convention used by most media managers
            if (_yearPresentRe.IsMatch(groupName))
                return 0.75;
            // Folder name only — title is plausible but year is unknown
            return 0.55;
        }

        private static double ComputeLeafConfidence(
            FolderSignal folder, TagSignal? tag)
        {
            double score = 0.5;
            if (tag?.Title is not null) score += 0.25;

            // A season+episode number parsed directly from the filename (the standard
            // Sonarr/Radarr "Show - S01E02 - Title" convention) is structurally
            // unambiguous on its own -- the file's identity (which show, which episode)
            // isn't in question here, only how much extra metadata (synopsis, cast, a
            // cleaned title) is available for it, and that gets filled in later by
            // enrichment regardless of this score. Without this, a show with no embedded
            // tags scored ~0.5-0.75 -- below the default 80% auto-import threshold --
            // meaning every one of its episodes was silently skipped by both the nightly
            // scheduled scan and a manual "Scan Now", even though the file itself was
            // never in doubt. Confirmed 2026-08-24: a real library scan skipped hundreds
            // of correctly-named episodes this way.
            if (folder.DetectedSeason.HasValue && folder.DetectedEpisode.HasValue)
                score += 0.35;

            return Math.Clamp(score, 0.0, 1.0);
        }

        private static List<string> BuildSources(
            FolderSignal folder, TagSignal? tag, int level)
        {
            var sources = new List<string> { "folder" };
            if (tag is not null) sources.Add("tags");
            return sources;
        }

        private static void RollUpConfidence(ScanGroup group)
        {
            if (group.Children.Count == 0) return;
            foreach (var child in group.Children) RollUpConfidence(child);
            group.ConfidenceScore = group.Children.Average(c => c.ConfidenceScore);
            if (group.ConfidenceCap is { } cap) group.ConfidenceScore = Math.Min(group.ConfidenceScore, cap);
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
