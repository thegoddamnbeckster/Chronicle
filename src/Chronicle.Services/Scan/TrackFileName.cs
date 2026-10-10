using System.Text.RegularExpressions;

namespace Chronicle.Services.Scan
{
    /// <summary>What can be read from a music file's name when it has no usable tags.</summary>
    public sealed record TrackNameParts(int? Disc, int? Track, string? Artist, string Title);

    /// <summary>
    /// Reads "01 - Title", "01. Title", "1-02 Title", "Track 05 - Title", and "Artist - 01 - Title". ("Artist - Title" with no number is left alone: a dash in a title is common.)
    /// Deliberately conservative about what counts as a track number, because titles that start with a number are real
    /// ("99 Problems", "7 Years", "1999"): a bare number followed by a space is only a track number when it is zero-padded
    /// ("01 Title") or followed by an explicit separator ("1. Title", "1 - Title").
    /// </summary>
    public static class TrackFileName
    {
        /// <summary>Audio file extensions; names of anything else (TV episodes!) are never reinterpreted as tracks.</summary>
        public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wma", ".aac", ".wav", ".aiff", ".ape", ".mpc", ".wv",
        };

        private static readonly Regex DiscTrack = new(@"^(\d)[-.](\d{2})(?:\s*[-._)]+\s*|\s+)(.+)$", RegexOptions.Compiled);
        private static readonly Regex TrackWord = new(@"^[Tt]rack\s*(\d{1,3})(?:\s*[-._)]+\s*|\s+)?(.*)$", RegexOptions.Compiled);
        private static readonly Regex ArtistTrackTitle = new(@"^(.+?)\s+-\s+(\d{1,3})\s*[-.]?\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex Separated = new(@"^(\d{1,3})\s*[-._)]+\s*(.+)$", RegexOptions.Compiled);
        private static readonly Regex Padded = new(@"^(0\d{1,2})\s+(.+)$", RegexOptions.Compiled);

        public static TrackNameParts Parse(string stem)
        {
            var s = stem.Trim();
            if (s.Length == 0) return new TrackNameParts(null, null, null, stem);

            var m = DiscTrack.Match(s);
            if (m.Success)
                return new TrackNameParts(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), null, m.Groups[3].Value.Trim());

            m = TrackWord.Match(s);
            if (m.Success && m.Groups[2].Value.Trim().Length > 0)
                return new TrackNameParts(null, int.Parse(m.Groups[1].Value), null, m.Groups[2].Value.Trim());

            m = Separated.Match(s);
            if (m.Success)
                return new TrackNameParts(null, int.Parse(m.Groups[1].Value), null, m.Groups[2].Value.Trim());

            m = Padded.Match(s);
            if (m.Success)
                return new TrackNameParts(null, int.Parse(m.Groups[1].Value), null, m.Groups[2].Value.Trim());

            m = ArtistTrackTitle.Match(s);
            if (m.Success)
                return new TrackNameParts(null, int.Parse(m.Groups[2].Value), m.Groups[1].Value.Trim(), m.Groups[3].Value.Trim());

            return new TrackNameParts(null, null, null, s);
        }
    }
}
