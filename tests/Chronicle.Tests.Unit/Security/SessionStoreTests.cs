using Chronicle.Services.Security;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Security
{
    public class SessionStoreTests
    {
        private sealed class FakeClock : TimeProvider
        {
            private DateTimeOffset _now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }

        private sealed class FixedPolicy(SessionPolicy policy) : ISessionPolicyProvider
        {
            public SessionPolicy Current { get; set; } = policy;
        }

        private readonly FakeClock _clock = new();
        private readonly FixedPolicy _policy = new(new SessionPolicy(TimeSpan.FromHours(24), TimeSpan.FromDays(30)));
        private readonly SessionStore _store;

        public SessionStoreTests() => _store = new SessionStore(_policy, _clock);

        [Fact]
        public void Create_ReturnsPrefixedHighEntropyKey()
        {
            var (key, _) = _store.Create(1, "ua", "10.0.0.5");

            key.Should().StartWith(SessionStore.KeyPrefix);
            // 32 random bytes -> 43 base64url chars after the prefix
            key.Length.Should().Be(SessionStore.KeyPrefix.Length + 43);
        }

        [Fact]
        public void Create_TwoSessionsForOneUser_GetDistinctKeysAndIds()
        {
            var (k1, s1) = _store.Create(1, null, null);
            var (k2, s2) = _store.Create(1, null, null);

            k1.Should().NotBe(k2);
            s1.SessionId.Should().NotBe(s2.SessionId);
            _store.Validate(k1, true).IsValid.Should().BeTrue();
            _store.Validate(k2, true).IsValid.Should().BeTrue();
        }

        [Fact]
        public void Validate_UnknownKey_IsRejected()
        {
            var result = _store.Validate("chr_sess_notarealkey", true);

            result.IsValid.Should().BeFalse();
            result.Rejection.Should().Be(SessionRejection.Unknown);
        }

        [Theory]
        [InlineData("")]
        [InlineData("eyJhbGciOiJIUzI1NiJ9.e30.abc")]   // a leftover JWT
        [InlineData("chr_live_abcdef")]                // an API key is not a session key
        public void Validate_WrongShape_IsRejectedWithoutError(string key)
        {
            _store.Validate(key, true).IsValid.Should().BeFalse();
        }

        [Fact]
        public void Validate_DoesNotExposeTheKeyInSessionInfo()
        {
            var (key, session) = _store.Create(1, "ua", "ip");

            session.ToString().Should().NotContain(key);
            _store.Validate(key, true).Session!.ToString().Should().NotContain(key);
        }

        [Fact]
        public void Revoke_EndsOnlyThatSession()
        {
            var (k1, s1) = _store.Create(1, null, null);
            var (k2, _) = _store.Create(1, null, null);

            _store.Revoke(s1.SessionId).Should().BeTrue();

            _store.Validate(k1, true).IsValid.Should().BeFalse();
            _store.Validate(k2, true).IsValid.Should().BeTrue();
            _store.Revoke(s1.SessionId).Should().BeFalse("it is already gone");
        }

        [Fact]
        public void RevokeByKey_EndsTheSession()
        {
            var (key, _) = _store.Create(1, null, null);

            _store.RevokeByKey(key).Should().BeTrue();
            _store.Validate(key, true).IsValid.Should().BeFalse();
        }

        [Fact]
        public void RevokeAllForUser_LeavesOtherUsersAlone()
        {
            var (a1, _) = _store.Create(1, null, null);
            var (a2, _) = _store.Create(1, null, null);
            var (b, _) = _store.Create(2, null, null);

            _store.RevokeAllForUser(1).Should().Be(2);

            _store.Validate(a1, true).IsValid.Should().BeFalse();
            _store.Validate(a2, true).IsValid.Should().BeFalse();
            _store.Validate(b, true).IsValid.Should().BeTrue();
        }

        [Fact]
        public void RevokeAllForUser_CanSpareTheCurrentSession()
        {
            var (keep, keepSession) = _store.Create(1, null, null);
            var (drop, _) = _store.Create(1, null, null);

            _store.RevokeAllForUser(1, exceptSessionId: keepSession.SessionId).Should().Be(1);

            _store.Validate(keep, true).IsValid.Should().BeTrue();
            _store.Validate(drop, true).IsValid.Should().BeFalse();
        }

        [Fact]
        public void IdleTimeout_EndsTheSession()
        {
            var (key, _) = _store.Create(1, null, null);

            _clock.Advance(TimeSpan.FromHours(24));

            var result = _store.Validate(key, true);
            result.IsValid.Should().BeFalse();
            result.Rejection.Should().Be(SessionRejection.IdleExpired);
            _store.Count.Should().Be(0, "an expired session is removed when it is noticed");
        }

        [Fact]
        public void Activity_RestartsTheIdleClock()
        {
            var (key, _) = _store.Create(1, null, null);

            _clock.Advance(TimeSpan.FromHours(20));
            _store.Validate(key, touch: true).IsValid.Should().BeTrue();
            _clock.Advance(TimeSpan.FromHours(20));

            _store.Validate(key, true).IsValid.Should().BeTrue("the 20h gap is shorter than the 24h idle limit");
        }

        [Fact]
        public void BackgroundPolling_DoesNotRestartTheIdleClock()
        {
            var (key, _) = _store.Create(1, null, null);

            // An abandoned tab keeps polling every few minutes for a day.
            for (var i = 0; i < 24; i++)
            {
                _clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));
                _store.Validate(key, touch: false);
                _clock.Advance(TimeSpan.FromSeconds(1));
            }

            var result = _store.Validate(key, touch: false);
            result.IsValid.Should().BeFalse("polling alone must not keep a session alive");
            result.Rejection.Should().Be(SessionRejection.IdleExpired);
        }

        [Fact]
        public void AbsoluteLifetime_EndsAnActiveSession()
        {
            var (key, _) = _store.Create(1, null, null);

            // Stay active every 12h for 30 days: idle never trips, the hard cap must.
            for (var i = 0; i < 59; i++)
            {
                _clock.Advance(TimeSpan.FromHours(12));
                _store.Validate(key, true).IsValid.Should().BeTrue();
            }
            _clock.Advance(TimeSpan.FromHours(12));

            var result = _store.Validate(key, true);
            result.IsValid.Should().BeFalse();
            result.Rejection.Should().Be(SessionRejection.LifetimeExpired);
        }

        [Fact]
        public void Policy_ChangesApplyToExistingSessions_ForIdle()
        {
            var (key, _) = _store.Create(1, null, null);
            _policy.Current = new SessionPolicy(TimeSpan.FromMinutes(10), TimeSpan.FromDays(30));

            _clock.Advance(TimeSpan.FromMinutes(11));

            _store.Validate(key, true).Rejection.Should().Be(SessionRejection.IdleExpired);
        }

        [Fact]
        public void Sweep_RemovesOnlyExpiredSessions()
        {
            var (old, _) = _store.Create(1, null, null);
            _clock.Advance(TimeSpan.FromHours(23));
            var (fresh, _) = _store.Create(2, null, null);
            _clock.Advance(TimeSpan.FromHours(2));

            _store.Sweep().Should().Be(1);

            _store.Count.Should().Be(1);
            _store.Validate(fresh, true).IsValid.Should().BeTrue();
            _store.Validate(old, true).IsValid.Should().BeFalse();
        }

        [Fact]
        public void PerUserCap_EvictsTheLeastRecentlySeen()
        {
            var keys = new List<string>();
            for (var i = 0; i < SessionStore.MaxSessionsPerUser; i++)
            {
                keys.Add(_store.Create(1, null, null).Key);
                _clock.Advance(TimeSpan.FromSeconds(1));
            }
            // Touch the oldest so the second-oldest becomes the least recently seen.
            _store.Validate(keys[0], true);
            _clock.Advance(TimeSpan.FromSeconds(1));

            var (newest, _) = _store.Create(1, null, null);

            _store.ListForUser(1).Count.Should().Be(SessionStore.MaxSessionsPerUser);
            _store.Validate(keys[1], true).IsValid.Should().BeFalse("the least recently seen was evicted");
            _store.Validate(keys[0], true).IsValid.Should().BeTrue();
            _store.Validate(newest, true).IsValid.Should().BeTrue();
        }

        [Fact]
        public void ListForUser_ReturnsOnlyThatUsersSessions_MostRecentFirst()
        {
            _store.Create(1, "first", null);
            _clock.Advance(TimeSpan.FromMinutes(1));
            _store.Create(1, "second", null);
            _store.Create(2, "other", null);

            var list = _store.ListForUser(1);

            list.Select(s => s.UserAgent).Should().Equal("second", "first");
        }

        [Fact]
        public void ClientInfo_IsTruncated()
        {
            var (_, session) = _store.Create(1, new string('x', 1000), new string('9', 500));

            session.UserAgent!.Length.Should().Be(200);
            session.RemoteIp!.Length.Should().Be(64);
        }

        [Fact]
        public void LogTag_IsShortAndDoesNotContainTheKey()
        {
            var (key, _) = _store.Create(1, null, null);

            var tag = SessionStore.LogTag(key);

            tag.Length.Should().Be(8);
            key.Should().NotContain(tag, "the tag is a hash prefix, not a slice of the key");
        }

        [Fact]
        public void NewStore_HasNoSessions_SoARestartEndsEverything()
        {
            var (key, _) = _store.Create(1, null, null);

            var afterRestart = new SessionStore(_policy, _clock);

            afterRestart.Validate(key, true).IsValid.Should().BeFalse();
            afterRestart.Count.Should().Be(0);
        }
    }
}
