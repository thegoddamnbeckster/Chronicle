using System.Xml.Linq;

namespace Chronicle.Services.Scan
{
    public class NfoSignal
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? ShowTitle { get; set; }
        public int? Year { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public string? ExternalId { get; set; }  // e.g. tmdb id from <uniqueid type="tmdb">
        public string? PosterUrl { get; set; }    // from <thumb> element
    }

    public class NfoSignalExtractor
    {
        /// <summary>Finds a .nfo sidecar next to <paramref name="filePath"/>.</summary>
        public string? FindSidecar(string filePath)
        {
            var dir  = Path.GetDirectoryName(filePath);
            var stem = Path.GetFileNameWithoutExtension(filePath);
            if (dir is null) return null;

            // Prefer "title.nfo" alongside the file
            var adjacent = Path.Combine(dir, stem + ".nfo");
            if (System.IO.File.Exists(adjacent)) return adjacent;

            // Fall back to any .nfo in the same folder
            try
            {
                return Directory.EnumerateFiles(dir, "*.nfo").FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>Extracts signal from a .nfo file path.</summary>
        public NfoSignal? Extract(string nfoPath)
        {
            if (!System.IO.File.Exists(nfoPath)) return null;
            try
            {
                return ParseXml(System.IO.File.ReadAllText(nfoPath));
            }
            catch { return null; }
        }

        /// <summary>Parses NFO XML string — exposed for unit testing.</summary>
        public NfoSignal? ParseXml(string xml)
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

                var signal = new NfoSignal
                {
                    Title     = Get("title"),
                    Artist    = Get("artist"),
                    Album     = Get("album"),
                    ShowTitle = Get("showtitle"),
                    Year      = GetInt("year"),
                    Season    = GetInt("season"),
                    Episode   = GetInt("episode"),
                    PosterUrl = Get("thumb"),
                };

                // <uniqueid type="tmdb">12345</uniqueid>
                var uid = root.Elements("uniqueid")
                    .FirstOrDefault(e =>
                        string.Equals(e.Attribute("type")?.Value, "tmdb",
                            StringComparison.OrdinalIgnoreCase));
                signal.ExternalId = uid?.Value?.Trim() is { Length: > 0 } id ? id : null;

                return signal;
            }
            catch { return null; }
        }
    }
}
