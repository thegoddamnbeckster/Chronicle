namespace Chronicle.Core.Models
{
    /// <summary>
    /// One MediaItem's outstanding "needs its local NFO (re)confirmed on Kodi" work item --
    /// the server-side coordination point that lets multiple Kodi instances sharing the same
    /// library (see [[project_kodi_device_ips]]: this user runs five -- upstairs, downstairs,
    /// storage, vision, office) divide up an NFO rebuild instead of each independently
    /// re-walking and re-deleting/rewriting the ENTIRE shared library's NFOs on every scan.
    ///
    /// Root-caused 2026-09-06: nfo_rebuild.py used to source its work list directly from each
    /// device's OWN `VideoLibrary.GetMovies/GetTVShows/GetEpisodes` (no explicit sort, so every
    /// invocation walked the exact same stable order starting at item #1) with zero persisted
    /// progress -- fine for an occasional manual run, but turning "run this after every scan"
    /// on for a library with tens of thousands of episodes meant every trigger re-did the whole
    /// multi-hour pass from scratch, and with several devices all defaulting to the same
    /// settings, they'd race each other rewriting the same shared NFO files. This table lets
    /// Chronicle -- the one thing every device already has in common -- track completion
    /// centrally: any device can claim a batch of pending items, and once ONE device confirms
    /// an item's NFO, no device needs to redo it (see NfoRebuildQueueService's own doc for the
    /// claim/lease/complete lifecycle).
    /// </summary>
    public class NfoRebuildQueueItem
    {
        public int Id { get; set; }
        public int MediaItemId { get; set; }

        /// <summary>"movie" | "tvshow" | "episode" -- which Kodi VideoLibrary type/Refresh*
        /// method this item needs, same vocabulary as KodiLibraryId.Kind.</summary>
        public string Kind { get; set; } = string.Empty;

        public DateTime EnqueuedAt { get; set; }

        /// <summary>Null when unclaimed (or the previous claim's lease has lapsed -- see
        /// LeaseExpiresAt). Set to the claiming device's own KodiDevice.Id while work is
        /// presumed in flight.</summary>
        public int? ClaimedByKodiDeviceId { get; set; }
        public DateTime? ClaimedAt { get; set; }

        /// <summary>A claim past this point is treated as abandoned (device crashed, lost
        /// network, or simply never called back) and becomes claimable again by any device --
        /// including the same one, if it just hadn't gotten around to it yet. Prevents one
        /// dropped connection from permanently stranding a queue item nobody will ever retry.</summary>
        public DateTime? LeaseExpiresAt { get; set; }

        /// <summary>Set once a device reports success -- see NfoRebuildQueueService.CompleteAsync.
        /// A completed item is never automatically re-queued (ordinary edits/ratings/watched
        /// changes now reach Kodi live via NfoPushService instead -- see MediaController/
        /// LibraryController/ScrobbleController); only an explicit "force full rebuild" clears
        /// this and re-seeds everything.</summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>Who most recently gave up on this item via ReleaseAsync (e.g. "not found in
        /// this device's own VideoLibrary"), and when. Root-caused live (2026-09-07): a device
        /// whose local library is a strict subset of the shared catalog (e.g. one Kodi instance
        /// with fewer movies mounted than another) would claim, fail to find, and release an
        /// item -- which the claim-eligibility query then let THAT SAME DEVICE immediately
        /// reclaim again on its very next batch, since a release fully clears
        /// ClaimedByKodiDeviceId with no memory of who just tried and failed. For a device stuck
        /// at the front of a long contiguous run of items it can never find locally, this spun
        /// forever: its own on-screen "items attempted" counter climbed into the hundreds while
        /// its real confirmed-completion count stayed near zero, because it kept re-claiming and
        /// re-releasing the exact same handful of rows instead of ever reaching new ones (or
        /// ceding them to a device that actually has the files). See ClaimBatchAsync's own doc
        /// for how these two fields close that loop.</summary>
        public int? LastReleasedByKodiDeviceId { get; set; }
        public DateTime? LastReleasedAt { get; set; }
    }
}
