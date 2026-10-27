namespace Chronicle.Core.Models
{
    /// <summary>
    /// A Kodi instance that has told Chronicle it exists (Chronicle_Scraper's movie add-on,
    /// lib/device_registration.py, once per start). Chronicle never calls a device, so the row
    /// only gives report-kodi-id and the refresh signal something to key a device's library ids on.
    /// Older add-on builds also sent the device's address, web-server port and login (Host, Port,
    /// Username, Password below); nothing reads them, and a newer build's registration clears them.
    /// Host is simply the address the registration arrived from.
    ///
    /// Keyed by the registering ApiTokenId, not UserId: one Chronicle user can have several
    /// Kodi instances (e.g. two Shields), each pairing with the device-auth flow gets its own
    /// ApiToken, and each token is exactly one physical device -- the natural upsert key,
    /// with no separate device-identity concept needed.
    /// </summary>
    public class KodiDevice
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public int ApiTokenId { get; set; }

        public string Name { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }

        /// <summary>Which add-on this row is for. A Kodi that has both Chronicle Scraper add-ons (and the
        /// scrobbler) holds one API key, and so one row, per add-on; these say which is which. Filled by
        /// the add-on's own registration, null for a row only known from its calls or from an older build.</summary>
        public string? AddonId { get; set; }
        /// <summary>The add-on's display name, e.g. "Chronicle Scraper (Movies)".</summary>
        public string? AddonName { get; set; }
        public string? AddonVersion { get; set; }
        /// <summary>The Kodi build the add-on runs in, as Kodi reports it.</summary>
        public string? KodiVersion { get; set; }

        public DateTime LastSeenAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
