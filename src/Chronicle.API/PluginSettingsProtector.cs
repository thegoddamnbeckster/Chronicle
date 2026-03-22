using Chronicle.Services.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Serilog;

namespace Chronicle.API;

/// <summary>
/// ASP.NET Core Data Protection-backed implementation of <see cref="IPluginSettingsProtector"/>.
/// Encrypts plugin settings JSON blobs before they are written to the database and decrypts them
/// on read, so that API keys and other credentials are never stored in plaintext.
/// </summary>
/// <remarks>
/// Data protection keys are persisted to disk (see Program.cs) so that settings survive
/// application restarts and database refreshes independently of the database file itself.
/// </remarks>
internal sealed class PluginSettingsProtector : IPluginSettingsProtector
{
    private const string Prefix = "ENC:";
    private readonly IDataProtector _protector;

    public PluginSettingsProtector(IDataProtectionProvider provider)
    {
        // Purpose string is stable — changing it would invalidate all existing encrypted values.
        _protector = provider.CreateProtector("Chronicle.PluginSettings.v1");
    }

    /// <inheritdoc/>
    public string Protect(string plainJson) =>
        Prefix + _protector.Protect(plainJson);

    /// <inheritdoc/>
    public string Unprotect(string? storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
            return "{}";

        if (storedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            try
            {
                return _protector.Unprotect(storedValue[Prefix.Length..]);
            }
            catch (Exception ex)
            {
                // Unprotect can fail if the keys were rotated / the key file was deleted.
                // Return an empty object so the plugin loads with no settings rather than crashing.
                Log.ForContext<PluginSettingsProtector>()
                   .Error(ex, "Failed to decrypt plugin settings — returning empty settings. " +
                              "Re-enter the plugin API key in Settings → Plugins to restore access.");
                return "{}";
            }
        }

        // Legacy plaintext — return as-is (will be encrypted on next save).
        return storedValue;
    }
}
