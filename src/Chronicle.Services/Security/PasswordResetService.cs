using System.Security.Cryptography;
using System.Text;
using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Chronicle.Services.Security
{
    public sealed record IssuedResetToken(string Token, DateTime ExpiresAtUtc, int UserId, string Username);

    /// <summary>An email that should be sent for a reset request. Produced inside the request, sent outside it.</summary>
    public sealed record PendingResetMail(EmailMessage Message, int UserId);

    public enum RedeemStatus { Ok, InvalidToken, WeakPassword }

    public sealed record RedeemResult(RedeemStatus Status, int UserId = 0, string? Username = null);

    public interface IPasswordResetService
    {
        /// <summary>Is outgoing email set up, i.e. can "forgot password" reach anyone?</summary>
        Task<bool> EmailConfiguredAsync(CancellationToken ct = default);

        /// <summary>Creates a token an administrator (or the local recovery command) hands over. Throws UserNotFoundException,
        /// or <see cref="InvalidOperationException"/> for a deactivated account.</summary>
        Task<IssuedResetToken> IssueAsync(int userId, int? issuedByUserId, string delivery, CancellationToken ct = default);

        /// <summary>Same, looked up by username (for the local recovery command).</summary>
        Task<IssuedResetToken> IssueForUsernameAsync(string username, string delivery, CancellationToken ct = default);

        /// <summary>
        /// A person asked for a reset link. Finds the matching active accounts by username or email and creates a token for each
        /// that has an address to send to. Returns the emails to send (empty when nothing matched or email is not set up) -
        /// the caller must not reveal which.
        /// </summary>
        Task<IReadOnlyList<PendingResetMail>> RequestByEmailAsync(string identifier, CancellationToken ct = default);

        Task<RedeemResult> RedeemAsync(string token, string newPassword, CancellationToken ct = default);

        string BuildResetUrl(string baseUrl, string token);
    }

    public sealed class PasswordResetService : IPasswordResetService
    {
        public const string TokenMinutesKey = "auth.reset_token_minutes";
        public const int MinPasswordLength = 8;
        public const string DeliveryEmail = "email";
        public const string DeliveryAdmin = "admin";
        public const string DeliveryConsole = "console";
        private const int MaxMatchesPerRequest = 3;

        private readonly ChronicleDbContext _db;
        private readonly IUserService _users;
        private readonly IEmailSettingsStore _emailSettings;
        private readonly ICachedAppSettings _settings;
        private readonly TimeProvider _clock;
        private readonly ILogger _log = Log.ForContext<PasswordResetService>();

        public PasswordResetService(ChronicleDbContext db, IUserService users, IEmailSettingsStore emailSettings,
            ICachedAppSettings settings, TimeProvider? clock = null)
        {
            _db = db;
            _users = users;
            _emailSettings = emailSettings;
            _settings = settings;
            _clock = clock ?? TimeProvider.System;
        }

        private TimeSpan Lifetime => TimeSpan.FromMinutes(_settings.Snapshot.GetPositive(TokenMinutesKey, 60));
        private DateTime Now => _clock.GetUtcNow().UtcDateTime;

        public async Task<bool> EmailConfiguredAsync(CancellationToken ct = default) =>
            (await _emailSettings.GetAsync(ct)).IsConfigured;

        // ── issuing ───────────────────────────────────────────────────────────

        public async Task<IssuedResetToken> IssueAsync(int userId, int? issuedByUserId, string delivery, CancellationToken ct = default)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new UserNotFoundException(userId);
            if (!user.IsActive) throw new InvalidOperationException("That account is deactivated; reactivate it before issuing a reset.");
            return await CreateAsync(user, issuedByUserId, delivery, ct);
        }

        public async Task<IssuedResetToken> IssueForUsernameAsync(string username, string delivery, CancellationToken ct = default)
        {
            var name = username.Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == name, ct)
                       ?? throw new InvalidOperationException($"There is no user named '{username}'.");
            if (!user.IsActive) throw new InvalidOperationException($"'{user.Username}' is deactivated; reactivate the account first.");
            return await CreateAsync(user, null, delivery, ct);
        }

        private async Task<IssuedResetToken> CreateAsync(User user, int? issuedBy, string delivery, CancellationToken ct)
        {
            var now = Now;

            // One live token per person: a new request replaces any earlier one, and long-dead rows are tidied away.
            var stale = await _db.PasswordResetTokens
                .Where(t => t.UserId == user.Id && t.UsedAt == null || t.ExpiresAt < now.AddDays(-1))
                .ToListAsync(ct);
            _db.PasswordResetTokens.RemoveRange(stale);

            var raw = Base64Url(RandomNumberGenerator.GetBytes(32));
            var expires = now + Lifetime;
            _db.PasswordResetTokens.Add(new PasswordResetToken
            {
                UserId = user.Id, TokenHash = Hash(raw), CreatedAt = now, ExpiresAt = expires,
                IssuedByUserId = issuedBy, Delivery = delivery,
            });
            await _db.SaveChangesAsync(ct);
            return new IssuedResetToken(raw, expires, user.Id, user.Username);
        }

        // ── "forgot password" ────────────────────────────────────────────────

        public async Task<IReadOnlyList<PendingResetMail>> RequestByEmailAsync(string identifier, CancellationToken ct = default)
        {
            var id = (identifier ?? "").Trim().ToLowerInvariant();
            if (id.Length == 0 || id.Length > 320) return [];

            var email = await _emailSettings.GetAsync(ct);
            if (!email.IsConfigured) return [];

            // Match on the login name, or on an email address the person has recorded.
            var matches = await _db.Users
                .Where(u => u.IsActive && (u.Username.ToLower() == id
                                           || (u.Email != null && u.Email.ToLower() == id)
                                           || u.Contacts.Any(c => c.Kind == "email" && c.Value.ToLower() == id)))
                .OrderBy(u => u.Id)
                .Take(MaxMatchesPerRequest)
                .Include(u => u.Contacts)
                .ToListAsync(ct);

            var mails = new List<PendingResetMail>();
            foreach (var user in matches)
            {
                var address = AddressFor(user);
                if (address is null)
                {
                    _log.Information("Password reset asked for user id {UserId} but they have no email address on file", user.Id);
                    continue;
                }

                var token = await CreateAsync(user, null, DeliveryEmail, ct);
                var minutes = (int)Math.Round(Lifetime.TotalMinutes);
                var url = BuildResetUrl(email.PublicUrl!, token.Token);
                mails.Add(new PendingResetMail(new EmailMessage(address, "Reset your Chronicle password",
                    $"Hello {user.ResolveDisplayName()},\n\n" +
                    "Someone asked to reset the password for your Chronicle account. To choose a new one, open this link:\n\n" +
                    $"{url}\n\n" +
                    $"The link works once and expires in {minutes} minutes. If you did not ask for this, ignore this message - your password has not changed.\n"),
                    user.Id));
            }
            return mails;
        }

        /// <summary>The address to write to: the primary email contact, else any email contact, else the account's own email.</summary>
        internal static string? AddressFor(User user)
        {
            var emails = user.Contacts.Where(c => c.Kind == "email").OrderByDescending(c => c.IsPrimary).ThenBy(c => c.Id).Select(c => c.Value.Trim());
            return emails.Concat([user.Email?.Trim() ?? ""]).FirstOrDefault(a => a.Length > 0 && EmailSettingsStore.IsPlausibleAddress(a));
        }

        public string BuildResetUrl(string baseUrl, string token) =>
            // The token rides in the fragment, which browsers never send to a server, so it cannot land in access logs or Referer headers.
            $"{baseUrl.TrimEnd('/')}/reset-password#token={token}";

        // ── redeeming ─────────────────────────────────────────────────────────

        public async Task<RedeemResult> RedeemAsync(string token, string newPassword, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return new RedeemResult(RedeemStatus.InvalidToken);

            var hash = Hash(token.Trim());
            var now = Now;
            var row = await _db.PasswordResetTokens.Include(t => t.User)
                .FirstOrDefaultAsync(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now, ct);
            if (row?.User is null || !row.User.IsActive) return new RedeemResult(RedeemStatus.InvalidToken);

            // Checked after the token so a wrong token and a weak password are told apart only to someone who holds a real token.
            if (string.IsNullOrEmpty(newPassword) || newPassword.Length < MinPasswordLength)
                return new RedeemResult(RedeemStatus.WeakPassword);

            // Spend the token first: if setting the password then failed, the token is burnt rather than reusable.
            row.UsedAt = now;
            await _db.SaveChangesAsync(ct);
            await _users.ChangePasswordAsync(row.UserId, newPassword, ct);

            var others = await _db.PasswordResetTokens.Where(t => t.UserId == row.UserId && t.Id != row.Id).ToListAsync(ct);
            _db.PasswordResetTokens.RemoveRange(others);
            await _db.SaveChangesAsync(ct);

            return new RedeemResult(RedeemStatus.Ok, row.UserId, row.User.Username);
        }

        // ── helpers ───────────────────────────────────────────────────────────

        internal static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
