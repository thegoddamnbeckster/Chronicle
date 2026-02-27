using Chronicle.Core.Models;
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

            // Override config before the host builds
            builder.UseSetting("Security:JwtSecret", "integration-test-secret-32-characters-min");
            builder.UseSetting("Security:JwtExpirationHours", "1");

            builder.ConfigureServices(services =>
            {
                // Remove ALL EF Core DbContext registrations for ChronicleDbContext
                // (EF Core 9 registers multiple descriptors per provider)
                var toRemove = services
                    .Where(d =>
                        d.ServiceType == typeof(DbContextOptions<ChronicleDbContext>) ||
                        d.ServiceType == typeof(IDbContextOptionsConfiguration<ChronicleDbContext>))
                    .ToList();

                foreach (var d in toRemove)
                    services.Remove(d);

                // Register with InMemory provider
                services.AddDbContext<ChronicleDbContext>(opts =>
                    opts.UseInMemoryDatabase(_dbName));
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
