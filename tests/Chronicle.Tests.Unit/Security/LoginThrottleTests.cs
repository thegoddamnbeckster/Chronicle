using Chronicle.Services.Security;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Security
{
    public class LoginThrottleTests
    {
        private sealed class FakeClock : TimeProvider
        {
            private DateTimeOffset _now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }

        private sealed class FixedSettings(Dictionary<string, string>? values = null) : ICachedAppSettings
        {
            public Dictionary<string, string> Values { get; } = values ?? new();
            public IReadOnlyDictionary<string, string> Snapshot => Values;
        }

        private readonly FakeClock _clock = new();
        private readonly FixedSettings _settings = new();
        private readonly LoginThrottle _throttle;

        public LoginThrottleTests() => _throttle = new LoginThrottle(_settings, _clock);

        private void Fail(string address, string user, int times)
        {
            for (var i = 0; i < times; i++) _throttle.RecordLoginFailure(address, user);
        }

        [Fact]
        public void FreshCaller_IsAllowed()
        {
            _throttle.CheckLogin("10.0.0.5", "alice").Allowed.Should().BeTrue();
        }

        [Fact]
        public void FiveFailuresFromOneAddressForOneUser_BlockThatPair()
        {
            Fail("10.0.0.5", "alice", 4);
            _throttle.CheckLogin("10.0.0.5", "alice").Allowed.Should().BeTrue("four is under the limit");

            Fail("10.0.0.5", "alice", 1);

            var d = _throttle.CheckLogin("10.0.0.5", "alice");
            d.Allowed.Should().BeFalse();
            d.RetryAfter.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(15));
        }

        [Fact]
        public void TheBlockedPairDoesNotBlockOtherUsersOrOtherAddresses()
        {
            Fail("10.0.0.5", "alice", 5);

            _throttle.CheckLogin("10.0.0.5", "bob").Allowed.Should().BeTrue();
            _throttle.CheckLogin("10.0.0.6", "alice").Allowed.Should().BeTrue();
        }

        [Fact]
        public void OneAddressTryingManyUsernames_IsBlockedByTheAddressLimit()
        {
            for (var i = 0; i < 20; i++) _throttle.RecordLoginFailure("10.0.0.5", $"user{i}");

            _throttle.CheckLogin("10.0.0.5", "someone-new").Allowed.Should().BeFalse();
            _throttle.CheckLogin("10.0.0.99", "someone-new").Allowed.Should().BeTrue("other addresses are unaffected");
        }

        [Fact]
        public void ManyAddressesGuessingOneUser_AreBlockedByTheUserLimit()
        {
            for (var i = 0; i < 30; i++) _throttle.RecordLoginFailure($"10.0.1.{i}", "admin");

            _throttle.CheckLogin("10.0.9.9", "admin").Allowed.Should().BeFalse("30 failures against 'admin' from 30 addresses");
            _throttle.CheckLogin("10.0.9.9", "alice").Allowed.Should().BeTrue();
        }

        [Fact]
        public void TheBlockLifts_WhenTheOldestFailuresAgeOutOfTheWindow()
        {
            Fail("10.0.0.5", "alice", 5);
            _throttle.CheckLogin("10.0.0.5", "alice").Allowed.Should().BeFalse();

            _clock.Advance(TimeSpan.FromMinutes(15));

            _throttle.CheckLogin("10.0.0.5", "alice").Allowed.Should().BeTrue();
        }

        [Fact]
        public void RetryAfter_ShrinksAsTimePasses()
        {
            Fail("10.0.0.5", "alice", 5);
            var first = _throttle.CheckLogin("10.0.0.5", "alice").RetryAfter;

            _clock.Advance(TimeSpan.FromMinutes(5));

            _throttle.CheckLogin("10.0.0.5", "alice").RetryAfter.Should().BeLessThan(first);
        }

        [Fact]
        public void ASuccessfulLogin_ClearsThatUsersCounters()
        {
            Fail("10.0.0.5", "alice", 4);

            _throttle.RecordLoginSuccess("10.0.0.5", "alice");
            Fail("10.0.0.5", "alice", 4);

            _throttle.CheckLogin("10.0.0.5", "alice").Allowed.Should().BeTrue("the earlier four were forgiven");
        }

        [Fact]
        public void ASuccessfulLogin_DoesNotForgiveTheAddressCounter()
        {
            for (var i = 0; i < 19; i++) _throttle.RecordLoginFailure("10.0.0.5", $"user{i}");
            _throttle.RecordLoginSuccess("10.0.0.5", "alice");
            _throttle.RecordLoginFailure("10.0.0.5", "another");

            _throttle.CheckLogin("10.0.0.5", "x").Allowed.Should().BeFalse("the address still has 20 failures");
        }

        [Fact]
        public void UsernameMatchingIgnoresCaseAndSurroundingSpace()
        {
            Fail("10.0.0.5", "Alice", 3);
            Fail("10.0.0.5", " alice ", 2);

            _throttle.CheckLogin("10.0.0.5", "ALICE").Allowed.Should().BeFalse("'Alice', ' alice ' and 'ALICE' are one account");
        }

        [Fact]
        public void LimitsComeFromSettings_AndAppearWithoutARestart()
        {
            _settings.Values[LoginThrottle.MaxPerAddressAndUserKey] = "2";
            Fail("10.0.0.5", "alice", 2);

            _throttle.CheckLogin("10.0.0.5", "alice").Allowed.Should().BeFalse();
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-3")]
        [InlineData("abc")]
        [InlineData("")]
        public void ABadSetting_FallsBackToTheDefault_NeverDisablesTheLimit(string bad)
        {
            _settings.Values[LoginThrottle.MaxPerAddressAndUserKey] = bad;
            Fail("10.0.0.5", "alice", 5);

            _throttle.CheckLogin("10.0.0.5", "alice").Allowed.Should().BeFalse("the default of 5 applies");
        }

        [Fact]
        public void RegistrationIsLimitedPerAddress_PerHour()
        {
            for (var i = 0; i < 10; i++) _throttle.RecordRegistration("10.0.0.5");

            _throttle.CheckRegistration("10.0.0.5").Allowed.Should().BeFalse();
            _throttle.CheckRegistration("10.0.0.6").Allowed.Should().BeTrue();

            _clock.Advance(TimeSpan.FromHours(1));
            _throttle.CheckRegistration("10.0.0.5").Allowed.Should().BeTrue();
        }

        [Fact]
        public void GenericActionLimiter_IsIndependentPerActionAndAddress()
        {
            for (var i = 0; i < 3; i++) _throttle.RecordAction("pair-miss", "10.0.0.5");

            _throttle.CheckAction("pair-miss", "10.0.0.5", 3, TimeSpan.FromMinutes(15)).Allowed.Should().BeFalse();
            _throttle.CheckAction("pair-miss", "10.0.0.6", 3, TimeSpan.FromMinutes(15)).Allowed.Should().BeTrue();
            _throttle.CheckAction("pair-initiate", "10.0.0.5", 3, TimeSpan.FromMinutes(15)).Allowed.Should().BeTrue();
        }

        [Fact]
        public void NullAddressOrUsername_DoNotThrow()
        {
            _throttle.RecordLoginFailure(null, null);
            _throttle.CheckLogin(null, null).Allowed.Should().BeTrue();
            _throttle.CheckRegistration(null).Allowed.Should().BeTrue();
        }
    }
}
