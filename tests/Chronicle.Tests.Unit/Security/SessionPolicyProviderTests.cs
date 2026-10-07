using Chronicle.Services.Security;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Security
{
    public class SessionPolicyProviderTests
    {
        private static SessionPolicy Parse(params (string Key, string Value)[] rows) =>
            SessionPolicyProvider.Parse(rows.ToDictionary(r => r.Key, r => r.Value));

        [Fact]
        public void NoSettings_UsesDefaults()
        {
            Parse().Should().Be(SessionPolicy.Default);
        }

        [Fact]
        public void ValidSettings_AreApplied()
        {
            var policy = Parse((SessionPolicyProvider.IdleHoursKey, "2"), (SessionPolicyProvider.MaxDaysKey, "7"));

            policy.IdleTimeout.Should().Be(TimeSpan.FromHours(2));
            policy.MaxLifetime.Should().Be(TimeSpan.FromDays(7));
        }

        [Fact]
        public void FractionalValues_UseADotRegardlessOfLocale()
        {
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");   // decimal comma
                Parse((SessionPolicyProvider.IdleHoursKey, "1.5")).IdleTimeout.Should().Be(TimeSpan.FromHours(1.5));
            }
            finally { Thread.CurrentThread.CurrentCulture = original; }
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-5")]
        [InlineData("abc")]
        [InlineData("")]
        [InlineData("Infinity")]
        [InlineData("1e12")]
        public void UnusableValues_FallBackToTheDefault_NeverDisableExpiry(string bad)
        {
            var policy = Parse((SessionPolicyProvider.IdleHoursKey, bad), (SessionPolicyProvider.MaxDaysKey, bad));

            policy.Should().Be(SessionPolicy.Default);
        }
    }
}
