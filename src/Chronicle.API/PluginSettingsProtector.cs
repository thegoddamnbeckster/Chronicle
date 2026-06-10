using Chronicle.Services.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Serilog;

namespace Chronicle.API;

/// <summary>
/// Pass-through implementation of <see cref="IPluginSettingsProtector"/>.
/// Plugin settings are stored as plaintext JSON in the local SQLite database.
/// Encryption adds no meaningful security for a self-hosted single-user app — anyone
/// with filesystem read access has the database AND any key files, so the protection
/// is illusory while the key-rotation fragility is real (silently resets all credentials
/// whenever key files are lost). The JWT secret uses the same plaintext approach.
/// Legacy ENC: blobs written by the old Data Protection implementation are still
/// decrypted transparently on read so users don't lose settings during the upgrade.
/// </summary>
internal sealed class PluginSettingsProtector : IPluginSettingsProtector
{
    private const string EncPrefix = "ENC:";
    private readonly IDataProtector _legacyProtector;

    public PluginSettingsProtector(IDataProtectionProvider provider)
    {
        // Keep the legacy protector so we can still read ENC: blobs written before this change.
        _legacyProtector = provider.CreateProtector("Chronicle.PluginSettings.v1");
    }

    /// <inheritdoc/>
    /// Stores plaintext JSON — no encryption.
    public string Protect(string plainJson) => plainJson;

    /// <inheritdoc/>
    public string Unprotect(string? storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
            return "{}";

        // Transparently migrate legacy ENC: blobs written by the old Data Protection path.
        if (storedValue.StartsWith(EncPrefix, StringComparison.Ordinal))
        {
            try
            {
                return _legacyProtector.Unprotect(storedValue[EncPrefix.Length..]);
            }
            catch (Exception ex)
            {
                // Keys were rotated / deleted. The user will need to re-enter credentials once.
                // After that, settings are saved as plaintext and this branch is never hit again.
                Log.ForContext<PluginSettingsProtector>()
                   .Warning(ex, "Legacy encrypted plugin settings could not be decrypted — " +
                                "returning empty settings. Re-enter the plugin API key in " +
                                "Settings → Plugins to restore access.");
                return "{}";
            }
        }

        // Plaintext — return as-is.
        return storedValue;
    }
}
