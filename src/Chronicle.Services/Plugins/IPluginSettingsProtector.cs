namespace Chronicle.Services.Plugins;

/// <summary>
/// Encrypts and decrypts plugin settings JSON blobs for secure at-rest storage.
/// </summary>
/// <remarks>
/// Values written to <c>plugins.settings_json</c> are encrypted by <see cref="Protect"/> and
/// prefixed with <c>ENC:</c>. <see cref="Unprotect"/> strips the prefix and decrypts, or returns
/// the input unchanged when it is legacy plaintext — allowing transparent zero-downtime migration
/// of existing records without a data migration step.
/// </remarks>
public interface IPluginSettingsProtector
{
    /// <summary>
    /// Encrypts <paramref name="plainJson"/> and returns a portable ciphertext token
    /// prefixed with <c>"ENC:"</c>.
    /// </summary>
    string Protect(string plainJson);

    /// <summary>
    /// Returns the plaintext JSON for <paramref name="storedValue"/>.
    /// <list type="bullet">
    ///   <item>If the value starts with <c>"ENC:"</c> it is decrypted.</item>
    ///   <item>Otherwise (legacy plaintext) it is returned as-is so that
    ///         existing records continue to work before they are re-saved.</item>
    /// </list>
    /// Returns an empty JSON object (<c>"{}"</c>) when <paramref name="storedValue"/> is null or blank.
    /// </summary>
    string Unprotect(string? storedValue);
}
