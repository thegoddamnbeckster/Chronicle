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

        /// <summary>e.g. ["folder", "tags", "nfo"] — signals that contributed.</summary>
        public List<string> SignalSources { get; set; } = [];

        /// <summary>True if any two signal sources disagreed on the group name.</summary>
        public bool HasConflicts { get; set; }

        public List<ScanGroup> Children { get; set; } = [];

        /// <summary>Leaf files that belong directly to this group (flat-grouped types).</summary>
        public List<string> Files { get; set; } = [];

        /// <summary>Absolute path to the folder on disk that this group represents (root groups only).</summary>
        public string? FolderPath { get; set; }

        /// <summary>Total number of leaf files under this group (recursive).</summary>
        public int TotalFileCount =>
            Files.Count + Children.Sum(c => c.TotalFileCount);
    }
}
