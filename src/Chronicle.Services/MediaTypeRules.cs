using System.Text.RegularExpressions;
using Chronicle.Core.Models;

namespace Chronicle.Services
{
    /// <summary>What an administrator may enter for a media type, and the rule for each field. One place, so the API,
    /// the tests and the page's own hints agree.</summary>
    public sealed record MediaTypeInput(
        string? Name, string DisplayName, string? Description, int HierarchyLevels, IReadOnlyList<string> HierarchyLabels,
        string InteractionVerb, string ProgressUnit, bool SupportsCollections, bool IsTrackable, string? ScanStrategy, bool IsActive);

    public static class MediaTypeRules
    {
        public const int MaxLevels = 5;
        private static readonly Regex NameRule = new(@"^[a-z0-9][a-z0-9_-]{1,48}$", RegexOptions.Compiled);
        private static readonly Regex WordRule = new(@"^[a-z]{2,20}$", RegexOptions.Compiled);

        /// <summary>The verbs the interface knows how to turn into labels. Anything else is allowed (and gets neutral
        /// wording), but these are offered first.</summary>
        public static readonly string[] SuggestedVerbs = ["watched", "listened", "read", "played"];

        /// <summary>Null when valid, otherwise a sentence an administrator can act on.</summary>
        public static string? Validate(MediaTypeInput input, bool creating)
        {
            if (creating)
            {
                var name = (input.Name ?? "").Trim();
                if (!NameRule.IsMatch(name))
                    return "The internal name must be 2-49 characters: lowercase letters, digits, dashes or underscores, starting with a letter or digit (for example 'comics'). It cannot be changed later because plugins refer to it.";
            }

            var display = (input.DisplayName ?? "").Trim();
            if (display.Length is < 1 or > 60) return "The display name must be 1-60 characters.";
            if ((input.Description?.Length ?? 0) > 300) return "The description can be at most 300 characters.";

            if (input.HierarchyLevels is < 1 or > MaxLevels) return $"The number of levels must be between 1 and {MaxLevels}.";
            if (input.HierarchyLabels.Count != input.HierarchyLevels)
                return $"Give one label for each of the {input.HierarchyLevels} level(s), for example 'Show, Season, Episode'.";
            if (input.HierarchyLabels.Any(l => string.IsNullOrWhiteSpace(l) || l.Trim().Length > 30 || l.Contains(',')))
                return "Each level label must be 1-30 characters and cannot contain a comma.";

            if (!WordRule.IsMatch(input.InteractionVerb ?? ""))
                return "The action word must be 2-20 lowercase letters, in the past tense (for example 'watched', 'listened', 'read', 'played').";
            if (!WordRule.IsMatch(input.ProgressUnit ?? ""))
                return "The progress unit must be 2-20 lowercase letters (for example 'minutes', 'pages', 'tracks').";
            if (!ScanStrategies.IsKnown(input.ScanStrategy)) return $"Unknown scan style. Use one of: {string.Join(", ", ScanStrategies.All)}, or leave it blank.";
            return null;
        }

        public static string JoinLabels(IEnumerable<string> labels) => string.Join(",", labels.Select(l => l.Trim()));

        public static string[] SplitLabels(string? stored) =>
            string.IsNullOrWhiteSpace(stored) ? [] : stored.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}
