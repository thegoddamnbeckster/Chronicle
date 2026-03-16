namespace Chronicle.Core.Models.Scan
{
    public class ScanGroupResult
    {
        /// <summary>Root-level groups (Artist, Show, Audiobook title, etc.).</summary>
        public List<ScanGroup> Groups { get; set; } = [];

        /// <summary>Files that could not be attached to any group with sufficient confidence.</summary>
        public List<string> Ungrouped { get; set; } = [];

        public int TotalFiles { get; set; }
        public int TotalGroups => Groups.Count;
    }
}
