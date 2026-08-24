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
        /// <summary>
        /// Kodi's own reserved season/show-level NFO filenames -- never a legitimate
        /// per-file sidecar, so the "any .nfo in folder" fallback below must exclude them.
        /// Matching one of these instead of the true sidecar (e.g. a season NFO's own
        /// &lt;title&gt;Season 2&lt;/title&gt; for every episode in that season folder) silently
        /// overwrote every episode's parsed title with the season's -- confirmed 2026-08-24
        /// scanning a real library where every episode in "Citadel/Season 02" came out
        /// named "Season 2".
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex _seasonOrShowNfo =
            new(@"^(tvshow|season(-specials|-all)?\d*)\.nfo$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Finds a .nfo sidecar next to <paramref name="filePath"/>.</summary>
        public string? FindSidecar(string filePath)
        {
            var dir  = Path.GetDirectoryName(filePath);
            var stem = Path.GetFileNameWithoutExtension(filePath);
            if (dir is null) return null;

            // Prefer "title.nfo" alongside the file
            var adjacent = Path.Combine(dir, stem + ".nfo");
            if (System.IO.File.Exists(adjacent)) return adjacent;

            // Fall back to any OTHER .nfo in the same folder (e.g. a movie's own NFO
            // named differently from its video file) -- but never a season/show NFO,
            // which describes the whole folder, not this one file.
            try
            {
                return Directory.EnumerateFiles(dir, "*.nfo")
                    .FirstOrDefault(f => !_seasonOrShowNfo.IsMatch(Path.GetFileName(f)));
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
