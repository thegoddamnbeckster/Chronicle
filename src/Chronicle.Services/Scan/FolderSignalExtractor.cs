using System.Text.RegularExpressions;
using Chronicle.Core.Helpers;

namespace Chronicle.Services.Scan
{
    public class FolderSignal
    {
        /// <summary>Folder names from scan root down to the file's parent (not including file).</summary>
        public List<string> FolderNames { get; set; } = [];
        public string FileName { get; set; } = string.Empty;
        /// <summary>Number of folder levels between scan root and file (1 = file directly in root).</summary>
        public int HierarchyDepth { get; set; }
        public int? DetectedSeason { get; set; }
        public int? DetectedEpisode { get; set; }
        public int? DetectedTrackNumber { get; set; }
        public int? DetectedDiscNumber { get; set; }
    }

    public class FolderSignalExtractor
    {
        // Matches S01E01, S1E1, s01e01, etc.
        private static readonly Regex _episodeRegex =
            new(@"[Ss](\d{1,2})[Ee](\d{1,3})", RegexOptions.Compiled);

        // Matches leading track number like "01 Title" or "01 - Title"
        private static readonly Regex _trackRegex =
            new(@"^(\d{1,3})[\s\-\.]+", RegexOptions.Compiled);

        // Matches "Season 1", "Season 01"
        private static readonly Regex _seasonFolderRegex =
            new(@"[Ss]eason\s*(\d{1,2})", RegexOptions.Compiled);

        // Matches "Disc 1", "CD1", "Disc2"
        private static readonly Regex _discRegex =
            new(@"(?:[Dd]isc|CD)\s*(\d)", RegexOptions.Compiled);

        public FolderSignal Extract(string filePath, string scanRoot)
        {
            var signal = new FolderSignal();

            // Normalise separators
            var normalRoot = scanRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalFile = filePath;

            // Get relative path
            string relative;
            if (normalFile.StartsWith(normalRoot, StringComparison.OrdinalIgnoreCase))
                relative = normalFile[(normalRoot.Length + 1)..];
            else
                relative = normalFile;

            var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            signal.FileName = Path.GetFileNameWithoutExtension(parts[^1]);
            signal.FolderNames = parts.Length > 1 ? parts[..^1].ToList() : [];
            signal.HierarchyDepth = parts.Length; // 1 = file in root, 2 = one folder deep, etc.

            // Season folder detection
            foreach (var folder in signal.FolderNames)
            {
                var sm = _seasonFolderRegex.Match(folder);
                if (sm.Success && DigitParsingHelper.TryParseDigits(sm.Groups[1].Value, out var season))
                    signal.DetectedSeason = season;

                var dm = _discRegex.Match(folder);
                if (dm.Success && DigitParsingHelper.TryParseDigits(dm.Groups[1].Value, out var disc))
                    signal.DetectedDiscNumber = disc;
            }

            // Episode from filename
            var em = _episodeRegex.Match(signal.FileName);
            if (em.Success
                && DigitParsingHelper.TryParseDigits(em.Groups[1].Value, out var epSeason)
                && DigitParsingHelper.TryParseDigits(em.Groups[2].Value, out var episode))
            {
                signal.DetectedSeason ??= epSeason;
                signal.DetectedEpisode = episode;
            }

            // Track number from filename
            var tm = _trackRegex.Match(signal.FileName);
            if (tm.Success && DigitParsingHelper.TryParseDigits(tm.Groups[1].Value, out var track))
                signal.DetectedTrackNumber = track;

            return signal;
        }
    }
}
