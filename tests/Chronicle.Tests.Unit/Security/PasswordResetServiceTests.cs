using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Chronicle.Services.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Chronicle.Tests.Unit.Security
{
    public class PasswordResetServiceTests : IDisposable
    {
        private sealed class FakeClock : TimeProvider
        {
            private DateTimeOffset _now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }

        private sealed class FixedSettings : ICachedAppSettings
        {
            public Dictionary<string, string> Values { get; } = new();
            public IReadOnlyDictionary<string, string> Snapshot => Values;
        }

        private readonly ChronicleDbContext _db;
        private readonly FakeClock _clock = new();
        private readonly FixedSettings _settings = new();
        private readonly Mock<IPasswordHasher> _hasher = new();
        private readonly PasswordResetService _service;
        private readonly EmailSettingsStore _emailStore;

        public PasswordResetServiceTests()
        {
            _db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            _hasher.Setup(h => h.HashPassword(It.IsAny<string>())).Returns<string>(p => "hash:" + p);
            var users = new UserService(_db, _hasher.Object, new DeactivatedUserCache());
            _emailStore = new EmailSettingsStore(_db);
            _service = new PasswordResetService(_db, users, _emailStore, _settings, _clock);
        }

        public void Dispose() => _db.Dispose();

        // ── helpers ───────────────────────────────────────────────────────────

        private User AddUser(string name, string? email = null, bool active = true, bool admin = false)
        {
            var u = new User { Username = name, Email = email, PasswordHash = "hash:old", IsActive = active, IsAdmin = admin, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            _db.Users.Add(u);
            _db.SaveChanges();
            return u;
        }

        private void AddContact(User u, string kind, string value, bool primary = false)
        {
            _db.UserContacts.Add(new UserContact { UserId = u.Id, Kind = kind, Value = value, IsPrimary = primary, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            _db.SaveChanges();
        }

        private Task ConfigureEmailAsync(string publicUrl = "https://chronicle.example.com") =>
            _emailStore.SaveAsync(new EmailSettingsUpdate("smtp.example.com", 587, SmtpSecurity.StartTls, null, null, "chronicle@example.com", "Chronicle", publicUrl));

        private static string TokenFrom(PendingResetMail mail)
        {
            var body = mail.Message.TextBody;
            var i = body.IndexOf("#token=", StringComparison.Ordinal) + "#token=".Length;
            var j = body.IndexOfAny(['\n', ' ', '\r'], i);
            return body[i..j];
        }

        private string StoredHash(int userId) => _db.PasswordResetTokens.Single(t => t.UserId == userId).TokenHash;

        // ══ issuing for an administrator ══════════════════════════════════════

        [Fact]
        public async Task IssuedToken_IsLongRandomUrlSafe_AndOnlyItsHashIsStored()
        {
            var u = AddUser("alice");

            var issued = await _service.IssueAsync(u.Id, issuedByUserId: 99, PasswordResetService.DeliveryAdmin);

            issued.Token.Length.Should().BeGreaterThanOrEqualTo(43);
            issued.Token.Should().MatchRegex("^[A-Za-z0-9_-]+$");
            var row = _db.PasswordResetTokens.Single();
            row.TokenHash.Should().NotContain(issued.Token).And.HaveLength(64);
            row.TokenHash.Should().Be(PasswordResetService.Hash(issued.Token));
            row.IssuedByUserId.Should().Be(99);
            row.Delivery.Should().Be("admin");
            row.UsedAt.Should().BeNull();
        }

        [Fact]
        public async Task TwoTokens_AreDifferent()
        {
            var a = AddUser("a"); var b = AddUser("b");

            (await _service.IssueAsync(a.Id, null, "admin")).Token.Should().NotBe((await _service.IssueAsync(b.Id, null, "admin")).Token);
        }

        [Fact]
        public async Task Expiry_DefaultsToAnHour_AndComesFromSettings()
        {
            var u = AddUser("alice");
            (await _service.IssueAsync(u.Id, null, "admin")).ExpiresAtUtc.Should().Be(_clock.GetUtcNow().UtcDateTime.AddMinutes(60));

            _settings.Values[PasswordResetService.TokenMinutesKey] = "15";
            (await _service.IssueAsync(u.Id, null, "admin")).ExpiresAtUtc.Should().Be(_clock.GetUtcNow().UtcDateTime.AddMinutes(15));
        }

        [Fact]
        public async Task ANewToken_ReplacesTheEarlierOne()
        {
            var u = AddUser("alice");
            var first = await _service.IssueAsync(u.Id, null, "admin");
            var second = await _service.IssueAsync(u.Id, null, "admin");

            (await _service.RedeemAsync(first.Token, "a-new-password")).Status.Should().Be(RedeemStatus.InvalidToken);
            (await _service.RedeemAsync(second.Token, "a-new-password")).Status.Should().Be(RedeemStatus.Ok);
        }

        [Fact]
        public async Task OneUsersToken_DoesNotAffectAnothers()
        {
            var a = AddUser("a"); var b = AddUser("b");
            var ta = await _service.IssueAsync(a.Id, null, "admin");
            await _service.IssueAsync(b.Id, null, "admin");

            (await _service.RedeemAsync(ta.Token, "a-new-password")).Status.Should().Be(RedeemStatus.Ok);
            _db.PasswordResetTokens.Count(t => t.UserId == b.Id && t.UsedAt == null).Should().Be(1);
        }

        [Fact]
        public async Task Issuing_ForADeactivatedOrUnknownUser_IsRefused()
        {
            var off = AddUser("off", active: false);

            await _service.Invoking(s => s.IssueAsync(off.Id, null, "admin")).Should().ThrowAsync<InvalidOperationException>();
            await _service.Invoking(s => s.IssueAsync(12345, null, "admin")).Should().ThrowAsync<UserNotFoundException>();
            _db.PasswordResetTokens.Should().BeEmpty();
        }

        [Fact]
        public async Task IssuingByUsername_IgnoresCase_AndExplainsWhenThereIsNoSuchUser()
        {
            AddUser("Alice");

            (await _service.IssueForUsernameAsync("  aLiCe ", PasswordResetService.DeliveryConsole)).Username.Should().Be("Alice");
            (await _service.Invoking(s => s.IssueForUsernameAsync("nobody", "console")).Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("nobody");
        }

        [Fact]
        public async Task LongDeadTokens_AreTidiedAway_WhenANewOneIsMade()
        {
            var a = AddUser("a"); var b = AddUser("b");
            await _service.IssueAsync(a.Id, null, "admin");
            _clock.Advance(TimeSpan.FromDays(3));

            await _service.IssueAsync(b.Id, null, "admin");

            _db.PasswordResetTokens.Select(t => t.UserId).Should().Equal(b.Id);
        }

        // ══ redeeming ═════════════════════════════════════════════════════════

        [Fact]
        public async Task Redeeming_SetsThePassword_AndSpendsTheToken()
        {
            var u = AddUser("alice");
            var t = await _service.IssueAsync(u.Id, null, "admin");

            var result = await _service.RedeemAsync(t.Token, "brand-new-password");

            result.Status.Should().Be(RedeemStatus.Ok);
            result.UserId.Should().Be(u.Id);
            result.Username.Should().Be("alice");
            (await _db.Users.FindAsync(u.Id))!.PasswordHash.Should().Be("hash:brand-new-password");
            (await _service.RedeemAsync(t.Token, "another-password-1")).Status.Should().Be(RedeemStatus.InvalidToken, "single use");
            (await _db.Users.FindAsync(u.Id))!.PasswordHash.Should().Be("hash:brand-new-password", "the second attempt changed nothing");
        }

        [Fact]
        public async Task AnExpiredToken_IsRefused_AndChangesNothing()
        {
            var u = AddUser("alice");
            var t = await _service.IssueAsync(u.Id, null, "admin");
            _clock.Advance(TimeSpan.FromMinutes(61));

            (await _service.RedeemAsync(t.Token, "brand-new-password")).Status.Should().Be(RedeemStatus.InvalidToken);
            (await _db.Users.FindAsync(u.Id))!.PasswordHash.Should().Be("hash:old");
        }

        [Fact]
        public async Task ATokenIsStillGoodUntilTheLastMoment()
        {
            var u = AddUser("alice");
            var t = await _service.IssueAsync(u.Id, null, "admin");
            _clock.Advance(TimeSpan.FromMinutes(59));

            (await _service.RedeemAsync(t.Token, "brand-new-password")).Status.Should().Be(RedeemStatus.Ok);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-a-real-token")]
        public async Task GuessesAndBlanks_AreRefused(string token)
        {
            AddUser("alice");
            (await _service.RedeemAsync(token, "brand-new-password")).Status.Should().Be(RedeemStatus.InvalidToken);
        }

        [Fact]
        public async Task AnAbsurdlyLongToken_IsRefusedWithoutWork()
        {
            (await _service.RedeemAsync(new string('x', 5000), "brand-new-password")).Status.Should().Be(RedeemStatus.InvalidToken);
        }

        [Fact]
        public async Task SurroundingWhitespace_IsForgiven_BecauseCodesGetPastedWithIt()
        {
            var u = AddUser("alice");
            var t = await _service.IssueAsync(u.Id, null, "admin");

            (await _service.RedeemAsync($"  {t.Token}\n", "brand-new-password")).Status.Should().Be(RedeemStatus.Ok);
        }

        [Theory]
        [InlineData("")]
        [InlineData("short")]
        [InlineData("1234567")]
        public async Task AWeakPassword_IsRefused_WithoutBurningTheToken(string weak)
        {
            var u = AddUser("alice");
            var t = await _service.IssueAsync(u.Id, null, "admin");

            (await _service.RedeemAsync(t.Token, weak)).Status.Should().Be(RedeemStatus.WeakPassword);

            (await _db.Users.FindAsync(u.Id))!.PasswordHash.Should().Be("hash:old");
            (await _service.RedeemAsync(t.Token, "a-proper-password")).Status.Should().Be(RedeemStatus.Ok, "the person can simply try again");
        }

        [Fact]
        public async Task AWrongTokenWithAWeakPassword_DoesNotRevealThatTheTokenWasWrong_ButDoesNotSayItWasRight()
        {
            AddUser("alice");

            // A bad token is reported as a bad token whatever the password; weak-password feedback is only for real tokens.
            (await _service.RedeemAsync("nope", "x")).Status.Should().Be(RedeemStatus.InvalidToken);
        }

        [Fact]
        public async Task ADeactivatedAccount_CannotUseAnEarlierToken()
        {
            var u = AddUser("alice");
            var t = await _service.IssueAsync(u.Id, null, "admin");
            u.IsActive = false;
            await _db.SaveChangesAsync();

            (await _service.RedeemAsync(t.Token, "brand-new-password")).Status.Should().Be(RedeemStatus.InvalidToken);
            (await _db.Users.FindAsync(u.Id))!.PasswordHash.Should().Be("hash:old");
        }

        [Fact]
        public async Task Redeeming_LeavesNoOtherLiveTokensForThatUser()
        {
            var u = AddUser("alice");
            var t = await _service.IssueAsync(u.Id, null, "admin");
            // Simulate a second token that slipped in (for example two requests racing).
            _db.PasswordResetTokens.Add(new PasswordResetToken { UserId = u.Id, TokenHash = "ZZZ", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(1) });
            await _db.SaveChangesAsync();

            await _service.RedeemAsync(t.Token, "brand-new-password");

            _db.PasswordResetTokens.Where(x => x.UserId == u.Id && x.UsedAt == null).Should().BeEmpty();
        }

        // ══ "forgot password" by email ════════════════════════════════════════

        [Fact]
        public async Task WhenEmailIsNotSetUp_NothingIsSent_AndNoTokenIsMade()
        {
            AddUser("alice", "alice@example.com");

            (await _service.RequestByEmailAsync("alice")).Should().BeEmpty();
            _db.PasswordResetTokens.Should().BeEmpty();
            (await _service.EmailConfiguredAsync()).Should().BeFalse();
        }

        [Theory]
        [InlineData("alice")]
        [InlineData("ALICE")]
        [InlineData("  Alice ")]
        [InlineData("alice@example.com")]
        [InlineData("Alice@Example.COM")]
        public async Task ByUsernameOrEmail_IgnoringCase(string identifier)
        {
            await ConfigureEmailAsync();
            AddUser("alice", "alice@example.com");

            var mails = await _service.RequestByEmailAsync(identifier);

            mails.Should().ContainSingle();
            mails[0].Message.To.Should().Be("alice@example.com");
        }

        [Fact]
        public async Task ByAnEmailContact_AndThePrimaryOneIsWrittenTo()
        {
            await ConfigureEmailAsync();
            var u = AddUser("alice");
            AddContact(u, "email", "old@example.com");
            AddContact(u, "email", "main@example.com", primary: true);
            AddContact(u, "phone", "555-0100");

            var viaContact = await _service.RequestByEmailAsync("old@example.com");

            viaContact.Should().ContainSingle().Which.Message.To.Should().Be("main@example.com");
        }

        [Fact]
        public async Task NoAddressOnFile_MeansNoEmail_AndNoToken()
        {
            await ConfigureEmailAsync();
            AddUser("alice");

            (await _service.RequestByEmailAsync("alice")).Should().BeEmpty();
            _db.PasswordResetTokens.Should().BeEmpty();
        }

        [Fact]
        public async Task AMalformedStoredAddress_IsSkipped_NotSentTo()
        {
            await ConfigureEmailAsync();
            AddUser("alice", "not an address");

            (await _service.RequestByEmailAsync("alice")).Should().BeEmpty();
        }

        [Fact]
        public async Task UnknownDeactivatedAndBlankIdentifiers_GetNothing()
        {
            await ConfigureEmailAsync();
            AddUser("off", "off@example.com", active: false);

            foreach (var id in new[] { "nobody", "off", "off@example.com", "", "   ", new string('a', 400) })
                (await _service.RequestByEmailAsync(id)).Should().BeEmpty(id);
            _db.PasswordResetTokens.Should().BeEmpty();
        }

        [Fact]
        public async Task ASharedEmailAddress_ReachesEachAccountItBelongsTo_UpToACap()
        {
            await ConfigureEmailAsync();
            for (var i = 0; i < 5; i++) AddUser($"user{i}", "family@example.com");

            var mails = await _service.RequestByEmailAsync("family@example.com");

            mails.Should().HaveCount(3, "capped so one request cannot spray a mailbox");
            mails.Select(m => TokenFrom(m)).Distinct().Should().HaveCount(3);
        }

        [Fact]
        public async Task TheEmail_ContainsAWorkingLink_ThatPointsAtTheConfiguredAddress_WithTheTokenInTheFragment()
        {
            await ConfigureEmailAsync("https://chronicle.example.com/");
            var u = AddUser("alice", "alice@example.com");

            var mail = (await _service.RequestByEmailAsync("alice")).Single();

            mail.Message.TextBody.Should().Contain("https://chronicle.example.com/reset-password#token=");
            mail.Message.TextBody.Should().Contain("60 minutes").And.Contain("ignore this message");
            mail.Message.Subject.Should().Contain("Chronicle");
            (await _service.RedeemAsync(TokenFrom(mail), "brand-new-password")).Status.Should().Be(RedeemStatus.Ok);
            (await _db.Users.FindAsync(u.Id))!.PasswordHash.Should().Be("hash:brand-new-password");
        }

        [Fact]
        public async Task AskingTwice_OnlyTheLatestLinkWorks()
        {
            await ConfigureEmailAsync();
            AddUser("alice", "alice@example.com");
            var first = TokenFrom((await _service.RequestByEmailAsync("alice")).Single());
            var second = TokenFrom((await _service.RequestByEmailAsync("alice")).Single());

            (await _service.RedeemAsync(first, "brand-new-password")).Status.Should().Be(RedeemStatus.InvalidToken);
            (await _service.RedeemAsync(second, "brand-new-password")).Status.Should().Be(RedeemStatus.Ok);
        }

        [Fact]
        public async Task EmailTokens_AreMarkedAsEmailDelivered()
        {
            await ConfigureEmailAsync();
            var u = AddUser("alice", "alice@example.com");
            await _service.RequestByEmailAsync("alice");

            _db.PasswordResetTokens.Single(t => t.UserId == u.Id).Delivery.Should().Be("email");
            _db.PasswordResetTokens.Single().IssuedByUserId.Should().BeNull();
        }

        [Fact]
        public void BuildResetUrl_TrimsTheTrailingSlash_AndUsesAFragment()
        {
            _service.BuildResetUrl("https://x.example/", "TOK").Should().Be("https://x.example/reset-password#token=TOK");
        }

        // ══ the stored mail settings ══════════════════════════════════════════

        [Fact]
        public async Task EmailSettings_AreNotConfigured_UntilServerSenderAndPublicAddressAreAllKnown()
        {
            (await _emailStore.GetAsync()).IsConfigured.Should().BeFalse();

            await _emailStore.SaveAsync(new EmailSettingsUpdate("", 587, SmtpSecurity.StartTls, null, null, "", "Chronicle", null));
            (await _emailStore.GetAsync()).IsConfigured.Should().BeFalse();

            await ConfigureEmailAsync();
            (await _emailStore.GetAsync()).IsConfigured.Should().BeTrue();
        }

        [Fact]
        public async Task TheMailPassword_IsWriteOnly_KeptWhenOmitted_AndClearedWhenEmpty()
        {
            await _emailStore.SaveAsync(new EmailSettingsUpdate("smtp.example.com", 465, SmtpSecurity.Tls, "me", "s3cret", "c@example.com", "Chron", "https://c.example.com"));
            var read = await _emailStore.GetAsync();
            read.HasPassword.Should().BeTrue();
            read.ToString().Should().NotContain("s3cret", "the settings object handed to callers never carries the password");
            (await _emailStore.GetCredentialsAsync()).Password.Should().Be("s3cret");

            await _emailStore.SaveAsync(new EmailSettingsUpdate("smtp.example.com", 465, SmtpSecurity.Tls, "me", null, "c@example.com", "Chron", "https://c.example.com"));
            (await _emailStore.GetCredentialsAsync()).Password.Should().Be("s3cret", "null means keep");

            await _emailStore.SaveAsync(new EmailSettingsUpdate("smtp.example.com", 465, SmtpSecurity.Tls, "me", "", "c@example.com", "Chron", "https://c.example.com"));
            (await _emailStore.GetAsync()).HasPassword.Should().BeFalse();
        }

        [Fact]
        public async Task EmailSettings_RoundTrip_AndNormaliseTheAddress()
        {
            await _emailStore.SaveAsync(new EmailSettingsUpdate(" smtp.example.com ", 2525, SmtpSecurity.None, " bob ", null, " sender@example.com ", "  ", "https://chronicle.example.com/app/"));

            var s = await _emailStore.GetAsync();

            s.Host.Should().Be("smtp.example.com");
            s.Port.Should().Be(2525);
            s.Security.Should().Be("none");
            s.Username.Should().Be("bob");
            s.FromAddress.Should().Be("sender@example.com");
            s.FromName.Should().Be("Chronicle", "a blank name falls back to the default");
            s.PublicUrl.Should().Be("https://chronicle.example.com/app", "no trailing slash");
        }

        [Theory]
        [InlineData("smtp.example.com:587", 587, "starttls", "a@b.co", "https://c.example.com", "host")]
        [InlineData("smtp example.com", 587, "starttls", "a@b.co", "https://c.example.com", "host")]
        [InlineData("smtp.example.com", 0, "starttls", "a@b.co", "https://c.example.com", "port")]
        [InlineData("smtp.example.com", 70000, "starttls", "a@b.co", "https://c.example.com", "port")]
        [InlineData("smtp.example.com", 587, "ssl3", "a@b.co", "https://c.example.com", "Security")]
        [InlineData("smtp.example.com", 587, "starttls", "nope", "https://c.example.com", "address")]
        [InlineData("smtp.example.com", 587, "starttls", "", "https://c.example.com", "from")]
        [InlineData("smtp.example.com", 587, "starttls", "a@b.co", "", "public address")]
        [InlineData("smtp.example.com", 587, "starttls", "a@b.co", "ftp://c.example.com", "public address")]
        [InlineData("smtp.example.com", 587, "starttls", "a@b.co", "https://u:p@c.example.com", "public address")]
        [InlineData("smtp.example.com", 587, "starttls", "a@b.co", "https://c.example.com/?x=1", "public address")]
        [InlineData("smtp.example.com", 587, "starttls", "a@b.co", "https://c.example.com/#frag", "public address")]
        [InlineData("smtp.example.com", 587, "starttls", "a@b.co", "not a url", "public address")]
        public async Task BadMailSettings_AreRefusedWithAReadableReason_AndNothingIsSaved(string host, int port, string security, string from, string url, string mentions)
        {
            var act = () => _emailStore.SaveAsync(new EmailSettingsUpdate(host, port, security, null, "pw", from, "Chronicle", url));

            (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().ContainEquivalentOf(mentions);
            _db.AppSettings.Where(s => s.Key.StartsWith("email.")).Should().BeEmpty();
        }

        [Fact]
        public async Task AllMailSettingsCanBeClearedAtOnce()
        {
            await ConfigureEmailAsync();

            await _emailStore.SaveAsync(new EmailSettingsUpdate("", 587, SmtpSecurity.StartTls, null, "", "", "Chronicle", null));

            var s = await _emailStore.GetAsync();
            s.IsConfigured.Should().BeFalse();
            s.HasPassword.Should().BeFalse();
        }

        [Theory]
        [InlineData("a@b.co", true)]
        [InlineData("first.last+tag@sub.example.org", true)]
        [InlineData("Name <a@b.co>", false)]
        [InlineData("a@b.co, c@d.co", false)]
        [InlineData("a b@c.co", false)]
        [InlineData("plain", false)]
        [InlineData("", false)]
        public void AddressValidation(string address, bool ok)
        {
            EmailSettingsStore.IsPlausibleAddress(address).Should().Be(ok);
        }

        // ══ sending ═══════════════════════════════════════════════════════════

        private sealed class RecordingSender : IEmailSender
        {
            public List<(EmailMessage Message, SmtpCredentials Credentials)> Sent { get; } = [];
            public Exception? Throw { get; set; }
            public Task SendAsync(EmailMessage message, SmtpCredentials credentials, CancellationToken ct = default)
            {
                if (Throw is not null) throw Throw;
                lock (Sent) Sent.Add((message, credentials));
                return Task.CompletedTask;
            }
        }

        [Fact]
        public async Task TheDispatcher_SendsInTheBackground_WithTheStoredCredentials()
        {
            var name = Guid.NewGuid().ToString();
            var sender = new RecordingSender();
            await using var services = BuildProvider(name, sender);
            await SeedMailSettingsAsync(services);
            var dispatcher = new ResetMailDispatcher(services.GetRequiredService<IServiceScopeFactory>());

            dispatcher.Dispatch(new PendingResetMail(new EmailMessage("to@example.com", "Hi", "body"), 7));
            await dispatcher.WhenIdleAsync();

            sender.Sent.Should().ContainSingle();
            sender.Sent[0].Message.To.Should().Be("to@example.com");
            sender.Sent[0].Credentials.Settings.Host.Should().Be("smtp.example.com");
            sender.Sent[0].Credentials.Password.Should().Be("pw");
        }

        [Fact]
        public async Task ASendFailure_IsContained_NeverThrownAtTheCaller()
        {
            var sender = new RecordingSender { Throw = new EmailSendException("The mail server rejected the user name or password.") };
            await using var services = BuildProvider(Guid.NewGuid().ToString(), sender);
            await SeedMailSettingsAsync(services);
            var dispatcher = new ResetMailDispatcher(services.GetRequiredService<IServiceScopeFactory>());

            dispatcher.Dispatch(new PendingResetMail(new EmailMessage("to@example.com", "Hi", "body"), 7));
            await dispatcher.Invoking(d => d.WhenIdleAsync()).Should().NotThrowAsync();

            sender.Sent.Should().BeEmpty();
        }

        [Fact]
        public async Task AnUnexpectedFailure_IsAlsoContained()
        {
            var sender = new RecordingSender { Throw = new InvalidOperationException("boom") };
            await using var services = BuildProvider(Guid.NewGuid().ToString(), sender);
            var dispatcher = new ResetMailDispatcher(services.GetRequiredService<IServiceScopeFactory>());

            dispatcher.Dispatch(new PendingResetMail(new EmailMessage("to@example.com", "Hi", "body"), 7));

            await dispatcher.Invoking(d => d.WhenIdleAsync()).Should().NotThrowAsync();
        }

        private static ServiceProvider BuildProvider(string dbName, IEmailSender sender) =>
            new ServiceCollection()
                .AddDbContext<ChronicleDbContext>(o => o.UseInMemoryDatabase(dbName))
                .AddScoped<IEmailSettingsStore, EmailSettingsStore>()
                .AddSingleton(sender)
                .BuildServiceProvider();

        private static async Task SeedMailSettingsAsync(ServiceProvider services)
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IEmailSettingsStore>()
                .SaveAsync(new EmailSettingsUpdate("smtp.example.com", 587, SmtpSecurity.StartTls, "u", "pw", "c@example.com", "Chronicle", "https://c.example.com"));
        }
    }
}
