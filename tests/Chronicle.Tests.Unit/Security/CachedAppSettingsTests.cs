using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Unit.Security
{
    public class CachedAppSettingsTests
    {
        private static (CachedAppSettings Cache, ServiceProvider Services) Build(params (string Key, string Value)[] rows)
        {
            var dbName = Guid.NewGuid().ToString();
            var services = new ServiceCollection()
                .AddDbContext<ChronicleDbContext>(o => o.UseInMemoryDatabase(dbName))
                .BuildServiceProvider();
            using (var scope = services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                foreach (var (k, v) in rows) db.AppSettings.Add(new AppSetting { Key = k, Value = v });
                db.AppSettings.Add(new AppSetting { Key = "unrelated.key", Value = "x" });
                db.SaveChanges();
            }
            return (new CachedAppSettings(services.GetRequiredService<IServiceScopeFactory>()), services);
        }

        [Fact]
        public async Task Snapshot_ContainsOnlyAuthSettings()
        {
            var (cache, _) = Build(("auth.session_idle_hours", "2"));

            await cache.RefreshAsync();

            cache.Snapshot.Should().ContainKey("auth.session_idle_hours").And.NotContainKey("unrelated.key");
        }

        [Fact]
        public void BeforeTheFirstLoad_ReturnsImmediately_WithoutBlocking()
        {
            var (cache, _) = Build(("auth.session_idle_hours", "2"));

            var snapshot = cache.Snapshot;   // must not throw or wait; background load starts

            snapshot.Should().NotBeNull();
        }

        [Fact]
        public async Task SessionPolicy_ReadsThroughTheCache()
        {
            var (cache, _) = Build(("auth.session_idle_hours", "2"), ("auth.session_max_days", "7"));
            await cache.RefreshAsync();

            var policy = new SessionPolicyProvider(cache).Current;

            policy.IdleTimeout.Should().Be(TimeSpan.FromHours(2));
            policy.MaxLifetime.Should().Be(TimeSpan.FromDays(7));
        }

        [Fact]
        public async Task ADatabaseFailure_KeepsThePreviousValues()
        {
            var (cache, services) = Build(("auth.session_idle_hours", "2"));
            await cache.RefreshAsync();
            await services.DisposeAsync();   // the next refresh will fail

            await cache.RefreshAsync();

            cache.Snapshot.Should().ContainKey("auth.session_idle_hours");
        }

        [Fact]
        public void GetPositive_FallsBackForEveryUnusableValue()
        {
            var rows = new Dictionary<string, string>
            {
                ["ok"] = "1.5", ["zero"] = "0", ["neg"] = "-1", ["text"] = "abc", ["empty"] = "",
                ["inf"] = "Infinity", ["huge"] = "1e12",
            };

            rows.GetPositive("ok", 9).Should().Be(1.5);
            foreach (var k in new[] { "zero", "neg", "text", "empty", "inf", "huge", "missing" })
                rows.GetPositive(k, 9).Should().Be(9, $"'{k}' is not a usable value");
        }
    }
}
