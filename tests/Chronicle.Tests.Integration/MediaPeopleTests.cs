using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration
{
    // MediaController.GetPeople (GET /media/{id}/people) is the mirror of
    // PeopleController.GetCredits -- see PeopleTests.cs -- walking title -> people instead of
    // person -> titles. Per-user request (2026-08-30): "on the media item detail I would like
    // a section with the people involved."
    public class MediaPeopleTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;

        public MediaPeopleTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        private async Task<HttpClient> AuthedClientAsync()
        {
            var client = _factory.CreateClient();
            var username = $"mediapeople_{Guid.NewGuid():N}";
            var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { username, password = "Password123!" });
            var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("token").GetString()!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        [Fact]
        public async Task GetPeople_MergesRolesPerPerson_ExcludesUnresolved_OrdersByBilling()
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

                var moviesType = await db.MediaTypes.FirstOrDefaultAsync(t => t.Name == "movies")
                    ?? (await db.MediaTypes.AddAsync(new MediaType
                    {
                        Name = "movies", DisplayName = "Movies", HierarchyLevels = 1,
                        InteractionVerb = "watched", ProgressUnit = "minutes",
                        IsBuiltIn = true, IsActive = true, CreatedAt = DateTime.UtcNow,
                    })).Entity;
                var peopleType = await db.MediaTypes.FirstOrDefaultAsync(t => t.Name == "people")
                    ?? (await db.MediaTypes.AddAsync(new MediaType
                    {
                        Name = "people", DisplayName = "People", HierarchyLevels = 1,
                        InteractionVerb = "viewed", ProgressUnit = "percent",
                        IsBuiltIn = true, IsActive = true, CreatedAt = DateTime.UtcNow,
                    })).Entity;
                await db.SaveChangesAsync();

                var movie = new MediaItem
                {
                    MediaTypeId = moviesType.Id, Name = "Test Movie", Year = 2020, HierarchyLevel = 0,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                };
                // Billed second (BillingOrder 1) but holds two credits (Director + Writer) that
                // must collapse into a single card with both roles.
                var director = new MediaItem
                {
                    MediaTypeId = peopleType.Id, Name = "Director Writer", HierarchyLevel = 0,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                };
                // Billed first (BillingOrder 0).
                var leadActor = new MediaItem
                {
                    MediaTypeId = peopleType.Id, Name = "Lead Actor", HierarchyLevel = 0,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                };
                db.MediaItems.AddRange(movie, director, leadActor);
                await db.SaveChangesAsync();

                db.MediaCredits.AddRange(
                    new MediaCredit { MediaItemId = movie.Id, PersonMediaItemId = leadActor.Id, PersonName = "Lead Actor", Role = "Actor", BillingOrder = 0, Source = "test" },
                    new MediaCredit { MediaItemId = movie.Id, PersonMediaItemId = director.Id, PersonName = "Director Writer", Role = "Director", BillingOrder = 1, Source = "test" },
                    new MediaCredit { MediaItemId = movie.Id, PersonMediaItemId = director.Id, PersonName = "Director Writer", Role = "Writer", BillingOrder = 1, Source = "test" },
                    // Unresolved credit -- no linked person page, must not appear in the response.
                    new MediaCredit { MediaItemId = movie.Id, PersonMediaItemId = null, PersonName = "Unresolved Person", Role = "Actor", Source = "test" }
                );
                await db.SaveChangesAsync();

                var client = await AuthedClientAsync();
                var resp = await client.GetAsync($"/api/v1/media/{movie.Id}/people");
                resp.EnsureSuccessStatusCode();

                var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var items = body.RootElement.GetProperty("data").EnumerateArray().ToList();

                items.Should().HaveCount(2);
                items.Select(i => i.GetProperty("name").GetString()).Should().Equal("Lead Actor", "Director Writer");

                var directorEntry = items.Single(i => i.GetProperty("name").GetString() == "Director Writer");
                directorEntry.GetProperty("roles").EnumerateArray()
                    .Select(r => r.GetString()).Should().BeEquivalentTo("Director", "Writer");
            }
        }
    }
}
