using System.Xml.Linq;

namespace Chronicle.Services.Scan
{
    public class NfoActor
    {
        public string? Name { get; set; }
        public string? Role { get; set; }
    }

    /// <summary>
    /// Rich display fields parsed on demand from an NFO sidecar. Distinct from
    /// <see cref="NfoSignal"/>, which only extracts the handful of fields needed for
    /// scan-time matching. This is used to render the FileScanner detail card.
    /// </summary>
    public class NfoDetail
    {
        public string? Title { get; set; }
        public string? OriginalTitle { get; set; }
        public string? Plot { get; set; }
        public List<string> Genres { get; set; } = [];
        public double? Rating { get; set; }
        public string? Mpaa { get; set; }
        public string? Studio { get; set; }
        public int? RuntimeMinutes { get; set; }
        public string? Premiered { get; set; }
        public string? Director { get; set; }
        public List<string> Writers { get; set; } = [];
        public List<NfoActor> Actors { get; set; } = [];
        public string? CollectionName { get; set; }
    }

    public class NfoDetailParser
    {
        private const int MaxActors = 20;

        /// <summary>Parses a .nfo file path. Returns null if the file is missing or unparseable.</summary>
        public NfoDetail? Parse(string nfoPath)
        {
            if (!File.Exists(nfoPath)) return null;
            try
            {
                return ParseXml(File.ReadAllText(nfoPath));
            }
            catch { return null; }
        }

        /// <summary>Parses NFO XML string — exposed for unit testing.</summary>
        public NfoDetail? ParseXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                var doc  = XDocument.Parse(xml.Trim());
                var root = doc.Root;
                if (root is null) return null;

                string? Get(string name) =>
                    root.Element(name)?.Value?.Trim() is { Length: > 0 } v ? v : null;

                int? GetInt(string name) =>
                    int.TryParse(Get(name), out var n) ? n : null;

                var detail = new NfoDetail
                {
                    Title          = Get("title"),
                    OriginalTitle  = Get("originaltitle"),
                    Plot           = Get("plot"),
                    Mpaa           = Get("mpaa"),
                    Studio         = Get("studio"),
                    RuntimeMinutes = GetInt("runtime"),
                    Premiered      = Get("premiered"),
                    Director       = Get("director"),
                    Rating         = ParseRating(root),
                };

                detail.Genres = root.Elements("genre")
                    .Select(e => e.Value?.Trim())
                    .Where(v => !string.IsNullOrEmpty(v))
                    .Select(v => v!)
                    .ToList();

                detail.Writers = root.Elements("credits")
                    .Select(e => e.Value?.Trim())
                    .Where(v => !string.IsNullOrEmpty(v))
                    .Select(v => v!)
                    .ToList();

                detail.CollectionName = root.Element("set")?.Element("name")?.Value?.Trim() is { Length: > 0 } set
                    ? set
                    : null;

                detail.Actors = root.Elements("actor")
                    .Select(e => new
                    {
                        Actor = new NfoActor
                        {
                            Name = e.Element("name")?.Value?.Trim(),
                            Role = e.Element("role")?.Value?.Trim(),
                        },
                        Order = int.TryParse(e.Element("order")?.Value, out var o) ? o : int.MaxValue,
                    })
                    .Where(x => !string.IsNullOrEmpty(x.Actor.Name))
                    .OrderBy(x => x.Order)
                    .Take(MaxActors)
                    .Select(x => x.Actor)
                    .ToList();

                return detail;
            }
            catch { return null; }
        }

        /// <summary>
        /// Prefers the default/imdb rating from &lt;ratings&gt;, falls back to the
        /// legacy flat &lt;rating&gt; element.
        /// </summary>
        private static double? ParseRating(XElement root)
        {
            var ratings = root.Element("ratings");
            if (ratings is not null)
            {
                var chosen = ratings.Elements("rating")
                    .FirstOrDefault(r => string.Equals(r.Attribute("default")?.Value, "true",
                        StringComparison.OrdinalIgnoreCase))
                    ?? ratings.Elements("rating").FirstOrDefault();

                var value = chosen?.Element("value")?.Value;
                if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }

            var flat = root.Element("rating")?.Value;
            if (double.TryParse(flat, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var flatParsed))
                return flatParsed;

            return null;
        }
    }
}
