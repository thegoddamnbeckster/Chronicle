namespace Chronicle.Core.Models.Scan
{
    public class ScanGroup
    {
        /// <summary>Normalised key used to deduplicate groups (lowercase, trimmed).</summary>
        public string GroupKey { get; set; } = string.Empty;

        /// <summary>Display name derived from the strongest available signal.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>0 = Artist/Show, 1 = Album/Season, 2 = Track/Episode.</summary>
        public int HierarchyLevel { get; set; }

        public int? Year { get; set; }

        /// <summary>Episode/track/season number extracted from filename or tags.</summary>
        public int? Number { get; set; }

        /// <summary>Local path to a folder image (.jpg/.png) if one was found.</summary>
        public string? PosterPath { get; set; }

        /// <summary>0.0 – 1.0. Average of member file scores, penalised for conflicts.</summary>
        public double ConfidenceScore { get; set; }

        /// <summary>e.g. ["folder", "tags"] — signals that contributed.</summary>
        public List<string> SignalSources { get; set; } = [];

        /// <summary>True if any two signal sources disagreed on the group name.</summary>
        public bool HasConflicts { get; set; }

        /// <summary>Upper limit for <see cref="ConfidenceScore"/> after the roll-up from children. Set on groups whose
        /// identity was worked out from a download-style file name rather than a real folder, so they show up for review
        /// but stay below the automatic-import threshold.</summary>
        public double? ConfidenceCap { get; set; }

        public List<ScanGroup> Children { get; set; } = [];

        /// <summary>Leaf files that belong directly to this group (flat-grouped types).</summary>
        public List<string> Files { get; set; } = [];

        /// <summary>Absolute path to the folder on disk that this group represents (root groups only).</summary>
        public string? FolderPath { get; set; }

        /// <summary>Author / artist name, populated for audiobook and music groups.</summary>
        public string? Author { get; set; }

        /// <summary>Series name from the Grouping tag (iTunes ©grp / ID3 TIT1), populated for audiobooks.</summary>
        public string? Series { get; set; }

        /// <summary>For an automatic-detect scan: the media type this (root) group was sorted into.</summary>
        public int? MediaTypeId { get; set; }
        public string? MediaTypeName { get; set; }

        /// <summary>Supplemental files (subtitles, artwork, extras, booklets) found with this group. Only
        /// filled in when the caller asks the grouper to collect them; never importable items themselves.</summary>
        public List<string> RelatedFiles { get; set; } = [];

        /// <summary>When the files look like a different media type than the one being scanned: that type's id,
        /// name and the reason, for the review page and the scheduled scan to act on. Null = no mismatch seen.</summary>
        public int? SuggestedMediaTypeId { get; set; }
        public string? SuggestedMediaTypeName { get; set; }
        public string? SuggestedMediaTypeReason { get; set; }

        /// <summary>Total number of leaf files under this group (recursive).</summary>
        public int TotalFileCount =>
            Files.Count + Children.Sum(c => c.TotalFileCount);
    }
}
