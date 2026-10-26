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

        public DateTime LastSeenAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
