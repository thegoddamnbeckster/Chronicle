using Chronicle.Core.Models;
using Chronicle.Services.Security;
using Chronicle.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration
{
    /// <summary>
    /// Spins up the full ASP.NET Core pipeline with an isolated InMemory database.
    /// </summary>
    public class ChronicleApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                // The in-process test server reports no client address, so every test shares one
                // "address" and a few hundred registrations/logins would trip the real limits.
                // Tests of the throttle itself replace this with their own settings.
                services.AddSingleton<ICachedAppSettings>(TestAuthSettings.Permissive());

                // Remove ALL EF Core DbContext registrations for ChronicleDbContext
                // (EF Core 9 registers multiple descriptors per provider)
                var toRemove = services
                    .Where(d =>
                        d.ServiceType == typeof(DbContextOptions<ChronicleDbContext>) ||
                        d.ServiceType == typeof(IDbContextOptionsConfiguration<ChronicleDbContext>))
                    .ToList();

                foreach (var d in toRemove)
                    services.Remove(d);

                // Register with InMemory provider.
                // Suppress TransactionIgnoredWarning — InMemory doesn't support real
                // transactions but the production code uses them; tests still verify
                // the logical behaviour correctly.
                services.AddDbContext<ChronicleDbContext>(opts =>
                    opts.UseInMemoryDatabase(_dbName)
                        .ConfigureWarnings(w =>
                            w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
            });
        }

        /// <summary>Seed required data into the test database after factory creation.</summary>
        public void SeedDatabase()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            db.Database.EnsureCreated();

            if (!db.MediaTypes.Any())
            {
                db.MediaTypes.Add(new MediaType
                {
                    Id = 1, Name = "tv", DisplayName = "TV Shows",
                    HierarchyLevels = 3, HierarchyLabels = "Show,Season,Episode",
                    InteractionVerb = "watched", ProgressUnit = "minutes",
                    IsBuiltIn = true, IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
                db.SaveChanges();
            }
        }
    }
}

namespace Chronicle.Tests.Integration
{
    /// <summary>Fixed auth settings for tests, standing in for the app_settings-backed cache.</summary>
    public sealed class TestAuthSettings : ICachedAppSettings
    {
        public Dictionary<string, string> Values { get; } = new();
        public IReadOnlyDictionary<string, string> Snapshot => Values;

        /// <summary>Limits so high that unrelated tests never meet them.</summary>
        public static TestAuthSettings Permissive()
        {
            var s = new TestAuthSettings();
            s.Values[LoginThrottle.MaxPerAddressAndUserKey] = "100000";
            s.Values[LoginThrottle.MaxPerAddressKey] = "100000";
            s.Values[LoginThrottle.MaxPerUserKey] = "100000";
            s.Values[LoginThrottle.RegisterMaxKey] = "100000";
            s.Values[Chronicle.API.Controllers.DeviceAuthController.InitiatesPerHourKey] = "100000";
            s.Values[Chronicle.API.Controllers.DeviceAuthController.MissesPer15MinKey] = "100000";
            return s;
        }
    }
}
