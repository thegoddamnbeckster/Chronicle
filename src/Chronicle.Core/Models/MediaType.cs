namespace Chronicle.Core.Models
{
    public class MediaType
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }

        /// <summary>Number of hierarchy levels (e.g. TV = 3: show/season/episode).</summary>
        public int HierarchyLevels { get; set; } = 1;

        /// <summary>Comma-separated labels for each level (e.g. "Show,Season,Episode").</summary>
        public string? HierarchyLabels { get; set; }

        /// <summary>Verb used when the user interacts (e.g. "watched", "listened", "read").</summary>
        public string InteractionVerb { get; set; } = "watched";

        /// <summary>Unit of progress (e.g. "minutes", "pages", "percent").</summary>
        public string ProgressUnit { get; set; } = "minutes";

        public bool IsBuiltIn { get; set; } = false;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }
    }
}
