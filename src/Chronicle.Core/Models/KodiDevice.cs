namespace Chronicle.Core.Models
{
    /// <summary>
    /// A Kodi instance that has self-registered its own remote-control (JSON-RPC over HTTP)
    /// address, so Chronicle's server can push a freshly-built NFO straight to it instead of
    /// waiting for that Kodi instance to notice a change on its own (a manual/scheduled
    /// "Recreate local NFO" pass, or its next ordinary library scan). Registered by
    /// Chronicle_Scraper's movie addon (lib/device_registration.py) using whatever LAN IP it
    /// resolves for itself and Kodi's own configured webserver settings (read locally via
    /// xbmc.executeJSONRPC, which this addon always has access to regardless of whether
    /// Chronicle's server can reach it) -- skipped entirely when that Kodi instance has
    /// "Allow remote control via HTTP" turned off.
    ///
    /// Keyed by the registering ApiTokenId, not UserId: one Chronicle user can have several
    /// Kodi instances (e.g. two Shields), each pairing with the device-auth flow gets its own
    /// ApiToken, and each token is exactly one physical device -- the natural upsert key,
    /// with no separate device-identity concept needed.
    ///
    /// Password is stored as plaintext, matching Chronicle's own established policy for every
    /// other externally-supplied credential (see PluginSettingsProtector's own doc: encryption
    /// adds no real protection for a self-hosted single-user app where the database and any
    /// key files are equally reachable, while key rotation makes it strictly more fragile).
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
