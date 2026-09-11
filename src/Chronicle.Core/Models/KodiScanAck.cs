namespace Chronicle.Core.Models
{
    /// <summary>
    /// Tracks, per API token, the last time that caller acknowledged Chronicle's "new content
    /// is available" signal (see KodiDeviceService.SignalNewContentAsync/IsScanNeededAsync).
    ///
    /// Deliberately keyed by ApiTokenId directly, NOT by KodiDeviceId/KodiDevice -- that table
    /// is written only by device_registration.py's register(), which explicitly skips itself
    /// entirely when Kodi's "Allow remote control via HTTP" is off (see its own doc: there's
    /// nothing to register in that case, since KodiDevice originally existed only to hold a
    /// remote-control address for NfoPushService to push to). This scan-signal feature has no
    /// such requirement -- it's a pull, the addon polls Chronicle and runs the scan on itself
    /// locally -- so tying its own device-identity tracking to that same gated table would have
    /// silently reintroduced the exact "needs remote control" dependency this feature exists to
    /// avoid, for every user who never turned that setting on (including a vanilla, freshly
    /// installed Kodi, where it's off by default). One row per API token, created lazily on
    /// first acknowledgement; no row means "never acknowledged," same as a null timestamp would.
    /// </summary>
    public class KodiScanAck
    {
        public int ApiTokenId { get; set; }
        public DateTime? LastAckAt { get; set; }
    }
}
