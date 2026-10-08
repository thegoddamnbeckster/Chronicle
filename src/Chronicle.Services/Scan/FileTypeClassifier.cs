using Chronicle.Core.Models;

namespace Chronicle.Services.Scan
{
    /// <summary>
    /// Decides which media type a file belongs to, from what each type says its files look like
    /// (<c>media_types.ScanHintsJson</c>, editable on the Media Types page), instead of taking the folder's word for it.
    /// A file that matches a type's distinctive patterns (an episode code, a season folder) belongs to that type; otherwise
    /// it goes to the lowest-numbered type that lists its file extension; otherwise it has no type. Nothing in here names a
    /// particular type, so a user-made type takes part as soon as it has hints.
    /// </summary>
    public sealed class FileTypeClassifier
    {
        private readonly List<(MediaType Type, ScanHints Hints)> _candidates;

        /// <summary>Types that can be picked automatically: active, with hints, and scanned by hierarchy depth (types
        /// with a special scan style, such as audiobooks, are always chosen explicitly).</summary>
        public FileTypeClassifier(IEnumerable<MediaType> types)
        {
            _candidates = types
                .Where(t => t.IsActive && string.IsNullOrEmpty(t.ScanStrategy))
                .Select(t => (Type: t, Hints: ScanHints.Parse(t.ScanHintsJson)))
                .Where(x => x.Hints is not null)
                .Select(x => (x.Type, x.Hints!))
                .OrderBy(x => x.Type.Id)
                .ToList();
        }

        public bool HasCandidates => _candidates.Count > 0;

        /// <summary>The type for this file, or null when no type's hints recognise it.</summary>
        public MediaType? Classify(string path)
        {
            foreach (var (type, hints) in _candidates)
                if (hints.MatchesDistinctively(path)) return type;
            foreach (var (type, hints) in _candidates)
                if (hints.MatchesExtension(path)) return type;
            return null;
        }

        /// <summary>
        /// Splits files into one list per type. A file no type recognises (a subtitle, cover art, an unknown format) goes
        /// with the files around it: the type most of its folder's recognised files have, else the nearest folder above
        /// that has any. Files with nowhere to go are left out.
        /// </summary>
        public Dictionary<MediaType, List<string>> Partition(IEnumerable<string> paths)
        {
            var result = new Dictionary<MediaType, List<string>>();
            var unplaced = new List<string>();
            var perFolder = new Dictionary<string, Dictionary<MediaType, int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in paths)
            {
                var type = Classify(path);
                if (type is null) { unplaced.Add(path); continue; }
                if (!result.TryGetValue(type, out var list)) result[type] = list = [];
                list.Add(path);

                var dir = Path.GetDirectoryName(path) ?? "";
                if (!perFolder.TryGetValue(dir, out var counts)) perFolder[dir] = counts = [];
                counts[type] = counts.GetValueOrDefault(type) + 1;
            }

            foreach (var path in unplaced)
            {
                for (var dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                {
                    if (!perFolder.TryGetValue(dir, out var counts)) continue;
                    var type = counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key.Id).First().Key;
                    result[type].Add(path);
                    break;
                }
            }
            return result;
        }
    }
}
