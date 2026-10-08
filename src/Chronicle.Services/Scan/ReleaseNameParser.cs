using System.Text.RegularExpressions;

namespace Chronicle.Services.Scan
{
    /// <summary>What can be read from a download-style name.</summary>
    /// <param name="Title">The show or film name with the rest removed, or null when nothing usable is left.</param>
    /// <param name="LooksLikeRelease">True when the name carries quality/codec/source words (1080p, x264, BluRay...), i.e. it is
    /// a release name rather than a tidy "Title (Year)" folder, so cleaning it is safe.</param>
    public sealed record ReleaseNameInfo(string? Title, int? Year, int? Season, int? Episode, string? EpisodeTitle, bool LooksLikeRelease);

    /// <summary>
    /// Reads names like <c>Show.Name.S02E03.Episode.Title.720p.HDTV.x264-GRP</c>, <c>Movie.Name.2019.1080p.BluRay.x264</c> and
    /// <c>[Group] Show Name - 1x05 [1080p]</c>: the title is whatever comes before the first episode code, year or quality word.
    /// Used as a fallback when the folder structure says nothing useful (a downloads folder); tidy folders such as
    /// <c>Heat (1995)</c> are not touched.
    /// </summary>
    public static class ReleaseNameParser
    {
        // Resolution, source and codec words only ever appear in release names; finding one is what makes a name
        // "look like a release". (Ordinary title words such as Cam, Web, Dual, Internal or Multi are deliberately absent.)
        private const string StrongQuality =
            @"(?:480p|576p|720p|1080p|1080i|2160p|4k|uhd|bluray|blu-ray|bdrip|brrip|bdremux|webrip|web-dl|webdl|hdtv|pdtv|dvdrip|dvdscr|hdrip|hdcam|xvid|divx|x264|x265|h264|h265|h\.264|h\.265|hevc|avc|eac3|ac3|dts|dd5\.1|ddp5\.1)";

        // Words that end a title when they follow it in a release name, but are not proof of a release on their own.
        private const string WeakQuality = @"(?:proper|repack|extended|unrated|remastered|complete)";

        private static readonly Regex GroupPrefix = new(@"^\s*[\[\(][^\]\)]{1,40}[\]\)]\s*", RegexOptions.Compiled);
        private static readonly Regex EpisodeCode = new(@"(?<![A-Za-z0-9])[Ss](\d{1,2})[Ee](\d{1,3})(?:[Ee]\d{1,3})*(?![A-Za-z0-9])|(?<![A-Za-z0-9])(\d{1,2})[xX](\d{2,3})(?![A-Za-z0-9])", RegexOptions.Compiled);
        private static readonly Regex SeasonPack = new(@"(?<![A-Za-z0-9])(?:[Ss](\d{1,2})|[Ss]eason[ ._-]?(\d{1,2}))(?![A-Za-z0-9])", RegexOptions.Compiled);
        private static readonly Regex YearToken = new(@"(?<![A-Za-z0-9])[\(\[]?((?:19|20)\d{2})[\)\]]?(?![A-Za-z0-9])", RegexOptions.Compiled);
        private static readonly Regex StrongToken = new($@"(?<![A-Za-z0-9]){StrongQuality}(?![A-Za-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex QualityToken = new($@"(?<![A-Za-z0-9])(?:{StrongQuality}|{WeakQuality})(?![A-Za-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static ReleaseNameInfo Parse(string name)
        {
            var s = name.Trim();
            s = GroupPrefix.Replace(s, string.Empty);

            // Dots/underscores as word separators only when the name has no spaces (so "Mr. Robot" survives).
            if (!s.Contains(' ')) s = s.Replace('.', ' ').Replace('_', ' ');
            s = Regex.Replace(s, @"\s+", " ").Trim();

            var looksLikeRelease = StrongToken.IsMatch(s);

            var ep = EpisodeCode.Match(s);
            int? season = null, episode = null;
            if (ep.Success)
            {
                if (ep.Groups[1].Success) { season = int.Parse(ep.Groups[1].Value); episode = int.Parse(ep.Groups[2].Value); }
                else { season = int.Parse(ep.Groups[3].Value); episode = int.Parse(ep.Groups[4].Value); }
            }
            var pack = ep.Success ? Match.Empty : SeasonPack.Match(s);
            if (pack.Success) season = int.Parse(pack.Groups[1].Success ? pack.Groups[1].Value : pack.Groups[2].Value);

            // The title ends at the earliest of: episode code, season marker, year, quality word. Of several years before
            // that point the LAST is the release year ("Blade Runner 2049 2017 1080p" -> title "Blade Runner 2049").
            var limit = s.Length;
            if (ep.Success) limit = Math.Min(limit, ep.Index);
            if (pack.Success) limit = Math.Min(limit, pack.Index);
            var q = QualityToken.Match(s);
            if (q.Success && q.Index > 0) limit = Math.Min(limit, q.Index);

            var cut = limit;
            int? year = null;
            var yearMatch = YearToken.Matches(s).Cast<Match>().LastOrDefault(m => m.Index > 0 && m.Index < limit);
            if (yearMatch is not null) { cut = yearMatch.Index; year = int.Parse(yearMatch.Groups[1].Value); }

            var title = Clean(s[..cut]);
            // A name that is nothing but a quality word ("1080p") has no title. (A title that merely starts with one,
            // such as "Complete Unknown", is kept: only a quality word that is not first ends the title.)
            if (title is not null && QualityToken.Match(title) is { Success: true } only && only.Length == title.Length) title = null;
            string? episodeTitle = null;
            if (ep.Success)
            {
                var rest = s[(ep.Index + ep.Length)..];
                var end = rest.Length;
                var rq = QualityToken.Match(rest);
                if (rq.Success) end = Math.Min(end, rq.Index);
                var ry = YearToken.Match(rest);
                if (ry.Success) end = Math.Min(end, ry.Index);
                episodeTitle = Clean(rest[..end]);
                if (episodeTitle is { Length: 0 }) episodeTitle = null;
            }

            return new ReleaseNameInfo(title, year, season, episode, episodeTitle, looksLikeRelease);
        }

        private static string? Clean(string text)
        {
            var t = text.Trim(' ', '-', '.', '_', '(', '[', ')', ']');
            t = Regex.Replace(t, @"\s+", " ");
            return t.Length == 0 ? null : t;
        }
    }
}
