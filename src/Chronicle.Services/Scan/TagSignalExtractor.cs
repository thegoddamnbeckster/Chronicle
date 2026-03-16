using TagLib;

namespace Chronicle.Services.Scan
{
    public class TagSignal
    {
        public string? Artist { get; set; }
        public string? AlbumArtist { get; set; }
        public string? Album { get; set; }
        public string? Title { get; set; }
        public uint? TrackNumber { get; set; }
        public uint? DiscNumber { get; set; }
        public uint? Year { get; set; }
        public string? Genre { get; set; }
    }

    public class TagSignalExtractor
    {
        // File extensions TagLib can reliably read tags from
        private static readonly HashSet<string> _supported = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".m4a", ".mp4", ".mkv", ".ogg", ".opus",
            ".wma", ".aac", ".wav", ".aiff", ".ape", ".mpc",
        };

        /// <summary>
        /// Returns null if the file extension is unsupported, the file doesn't exist,
        /// or TagLib cannot read it — callers should treat null as "no tag signal".
        /// </summary>
        public TagSignal? Extract(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            if (!_supported.Contains(ext)) return null;
            if (!System.IO.File.Exists(filePath)) return null;

            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                var tag = tagFile.Tag;
                if (tag is null) return null;

                return new TagSignal
                {
                    Artist      = NullIfEmpty(tag.FirstPerformer ?? tag.JoinedPerformers),
                    AlbumArtist = NullIfEmpty(tag.FirstAlbumArtist ?? tag.JoinedAlbumArtists),
                    Album       = NullIfEmpty(tag.Album),
                    Title       = NullIfEmpty(tag.Title),
                    TrackNumber = tag.Track > 0 ? tag.Track : null,
                    DiscNumber  = tag.Disc > 0 ? tag.Disc : null,
                    Year        = tag.Year > 0 ? tag.Year : null,
                    Genre       = NullIfEmpty(tag.FirstGenre),
                };
            }
            catch
            {
                // TagLib throws on corrupt/unsupported files — treat as no signal
                return null;
            }
        }

        private static string? NullIfEmpty(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
