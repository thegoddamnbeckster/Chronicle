using System.Globalization;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services.Security
{
    /// <summary>How the connection to the mail server is secured.</summary>
    public static class SmtpSecurity
    {
        public const string None = "none";
        public const string StartTls = "starttls";
        public const string Tls = "tls";
        public static readonly string[] All = [None, StartTls, Tls];
    }

    /// <summary>Outgoing mail settings (Settings -> Email). The password is never part of what is read back out.</summary>
    public sealed record EmailSettings(
        string Host, int Port, string Security, string? Username, string FromAddress, string FromName,
        string? PublicUrl, bool HasPassword)
    {
        /// <summary>Mail can be sent only when the server, the sender AND the public address are known. The public address is
        /// required because the reset link must point at the real Chronicle: building it from the request's Host header would let
        /// anyone who can reach the server make it email a victim a link to a site of their choosing.</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress) && !string.IsNullOrWhiteSpace(PublicUrl);
        public static readonly EmailSettings Empty = new("", 587, SmtpSecurity.StartTls, null, "", "Chronicle", null, false);
    }

    /// <param name="Password">null = keep the saved password, "" = clear it, anything else = replace it.</param>
    public sealed record EmailSettingsUpdate(
        string Host, int Port, string Security, string? Username, string? Password, string FromAddress, string FromName, string? PublicUrl);

    /// <summary>Settings plus the password, for the sender only. Never serialised to a client.</summary>
    public sealed record SmtpCredentials(EmailSettings Settings, string? Password);

    public interface IEmailSettingsStore
    {
        Task<EmailSettings> GetAsync(CancellationToken ct = default);
        Task<SmtpCredentials> GetCredentialsAsync(CancellationToken ct = default);

        /// <summary>Validates and saves. Throws <see cref="ArgumentException"/> with a readable message for bad input.</summary>
        Task SaveAsync(EmailSettingsUpdate update, CancellationToken ct = default);
    }

    /// <summary>
    /// Stores the mail settings in <c>app_settings</c> under <c>email.*</c>. The password follows the same rule as plugin
    /// credentials in this application (stored as entered, not encrypted - the project deliberately dropped at-rest
    /// encryption because losing the key files silently wiped saved credentials), so it is guarded the other way:
    /// it is write-only through the API, and <c>email.*</c> rows are hidden from the general settings listing.
    /// </summary>
    public sealed class EmailSettingsStore : IEmailSettingsStore
    {
        public const string Prefix = "email.";
        private const string K_Host = "email.host", K_Port = "email.port", K_Security = "email.security",
            K_User = "email.username", K_Pass = "email.password", K_From = "email.from_address",
            K_Name = "email.from_name", K_Url = "email.public_url";

        private readonly ChronicleDbContext _db;
        public EmailSettingsStore(ChronicleDbContext db) => _db = db;

        public async Task<EmailSettings> GetAsync(CancellationToken ct = default) => (await GetCredentialsAsync(ct)).Settings;

        public async Task<SmtpCredentials> GetCredentialsAsync(CancellationToken ct = default)
        {
            var rows = await _db.AppSettings.AsNoTracking().Where(s => s.Key.StartsWith(Prefix)).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
            string? Get(string key) => rows.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

            var port = int.TryParse(Get(K_Port), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536 ? p : 587;
            var security = Get(K_Security) is { } s && SmtpSecurity.All.Contains(s) ? s : SmtpSecurity.StartTls;
            var password = Get(K_Pass);

            var settings = new EmailSettings(Get(K_Host) ?? "", port, security, Get(K_User), Get(K_From) ?? "",
                Get(K_Name) ?? "Chronicle", Get(K_Url), password is not null);
            return new SmtpCredentials(settings, password);
        }

        public async Task SaveAsync(EmailSettingsUpdate u, CancellationToken ct = default)
        {
            var host = (u.Host ?? "").Trim();
            var from = (u.FromAddress ?? "").Trim();
            if (host.Length > 0 && (host.Contains(' ') || host.Contains('/') || host.Contains(':')))
                throw new ArgumentException("The mail server must be a host name or address without a port, spaces or slashes (enter the port separately).");
            if (u.Port is < 1 or > 65535)
                throw new ArgumentException("The port must be between 1 and 65535.");
            if (!SmtpSecurity.All.Contains(u.Security))
                throw new ArgumentException($"Security must be one of: {string.Join(", ", SmtpSecurity.All)}.");
            if (from.Length > 0 && !IsPlausibleAddress(from))
                throw new ArgumentException("The 'from' address does not look like an email address.");
            if (host.Length > 0 && from.Length == 0)
                throw new ArgumentException("A 'from' address is required to send mail.");

            string? publicUrl = null;
            if (!string.IsNullOrWhiteSpace(u.PublicUrl))
            {
                if (!Uri.TryCreate(u.PublicUrl.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                    || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                    throw new ArgumentException("The public address must be a full http(s) address such as https://chronicle.example.com, without a path query or login details.");
                publicUrl = uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
            }

            if (host.Length > 0 && publicUrl is null)
                throw new ArgumentException("The public address of Chronicle is required (for example https://chronicle.example.com): it is what the link in a reset email points at.");

            await SetAsync(K_Host, host, ct);
            await SetAsync(K_Port, u.Port.ToString(CultureInfo.InvariantCulture), ct);
            await SetAsync(K_Security, u.Security, ct);
            await SetAsync(K_User, (u.Username ?? "").Trim(), ct);
            await SetAsync(K_From, from, ct);
            await SetAsync(K_Name, string.IsNullOrWhiteSpace(u.FromName) ? "Chronicle" : u.FromName.Trim(), ct);
            await SetAsync(K_Url, publicUrl ?? "", ct);
            if (u.Password is not null) await SetAsync(K_Pass, u.Password, ct);
            await _db.SaveChangesAsync(ct);
        }

        private async Task SetAsync(string key, string value, CancellationToken ct)
        {
            var row = await _db.AppSettings.FindAsync([key], ct);
            if (row is null) _db.AppSettings.Add(new AppSetting { Key = key, Value = value });
            else row.Value = value;
        }

        public static bool IsPlausibleAddress(string address)
        {
            try { var a = new System.Net.Mail.MailAddress(address); return a.Address == address && address.Contains('@') && !address.Any(char.IsWhiteSpace); }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { return false; }   // "" and null throw ArgumentException
        }
    }
}
