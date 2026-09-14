namespace Chronicle.Core.Models
{
    /// <summary>
    /// Maps one Chronicle MediaItem, on one specific Kodi instance, to that instance's own
    /// internal VideoLibrary id (movieid/tvshowid/episodeid) -- the one piece of information
    /// Chronicle's server structurally cannot derive on its own (Kodi assigns these ids itself,
    /// independently per instance, and never hands them to the scraper addon on any channel
    /// except a VideoLibrary lookup the addon already does for other reasons).
    ///
    /// Reported by Chronicle_Scraper (both addons) via POST .../scraper/report-kodi-id
    /// whenever an ordinary scan resolves an item's Kodi-side location anyway (movie addon:
    /// python/scraper.py's find_movie_location(); TV addon: find_show_location()/get_episode())
    /// -- not just during an explicit rebuild pass, so this mapping stays fresh even for
    /// devices that never run a rebuild.
    ///
    /// Originally required by NfoPushService to call VideoLibrary.RefreshMovie/RefreshTVShow/
    /// RefreshEpisode (all of which take Kodi's own internal id, not a file path) to make an
    /// ALREADY-IMPORTED item reconsider its local NFO -- removed 2026-09-13 along with the rest
    /// of the server-side NFO generation system, so this mapping has no current server-side
    /// consumer. Left in place (both the table and the report-kodi-id endpoint both Kodi addons
    /// still call on every ordinary scan) since it's a harmless write with no NFO/scanning
    /// implications, and a future feature may want the same "which Kodi id is this on which
    /// device" mapping again.
    /// </summary>
    public class KodiLibraryId
    {
        public int Id { get; set; }
        public int KodiDeviceId { get; set; }
        public int MediaItemId { get; set; }

        /// <summary>"movie" | "tvshow" | "episode" -- which Refresh* JSON-RPC method applies.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>Kodi's own movieid/tvshowid/episodeid on KodiDeviceId's instance.</summary>
        public int KodiId { get; set; }

        public DateTime UpdatedAt { get; set; }
    }
}
