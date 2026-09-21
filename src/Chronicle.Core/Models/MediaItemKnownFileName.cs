namespace Chronicle.Core.Models
{
    /// <summary>
    /// One base filename (via Path.GetFileName, not a full path -- Kodi's own scraper search
    /// only ever hands over the bare filename) known to belong to a MediaItem, via that item's
    /// own MetadataJson "fileScanner.filePaths" array. Kept in sync by FileScanService's own
    /// UpsertGroupItemAsync (the only writer of that array) every time it changes.
    ///
    /// Exists purely as a fast, indexed lookup for ScraperController.SearchMovies's own
    /// filename fast-path -- root-caused live (2026-09-19): that path previously ran a
    /// `LIKE '%filename%'` scan directly over MetadataJson, which SQLite can never use an
    /// index for (a leading-wildcard LIKE always requires a full scan), and MetadataJson holds
    /// everything about an item (cast, overview, every provider's own blob) -- confirmed live,
    /// ~389MB across ~6,150 movie-like items, a ~590ms floor on EVERY single filename search,
    /// for every movie, on every scan. FileName here is indexed and holds only the one thing
    /// that lookup actually needs.
    ///
    /// A filename is deliberately not unique across rows -- two different real files can share
    /// an exact basename in different folders (the exact shape of the file-scan corruption bug
    /// found live 2026-09-19, where a Futurama file and a Worst Cooks in America file both
    /// existed under folders literally named "Season 11"), so a lookup here can and does return
    /// more than one candidate; the caller (SearchMovies) still has its own year-mismatch guard
    /// to pick the right one.
    /// </summary>
    public class MediaItemKnownFileName
    {
        public int Id { get; set; }
        public int MediaItemId { get; set; }
        public string FileName { get; set; } = string.Empty;

        // Navigation
        public MediaItem? MediaItem { get; set; }
    }
}
