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
    public class PeopleTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;

        public PeopleTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        private async Task<HttpClient> AuthedClientAsync()
        {
            var client = _factory.CreateClient();
            var username = $"people_{Guid.NewGuid():N}";
            var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { username, password = "Password123!" });
            var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("token").GetString()!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        // Per-user request (2026-08-30): "can the credits be sorted by date, most recent
        // first please?" -- PeopleController.GetCredits previously returned each role
        // group's items in whatever order EF happened to materialize them.
        [Fact]
        public async Task GetCredits_OrdersItemsByYearDescending_NullYearLast()
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

                var person = new MediaItem
                {
                    MediaTypeId = peopleType.Id, Name = "Test Actor", HierarchyLevel = 0,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                };
                var oldMovie = new MediaItem
                {
                    MediaTypeId = moviesType.Id, Name = "Old Movie", Year = 1995, HierarchyLevel = 0,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                };
                var newMovie = new MediaItem
                {
                    MediaTypeId = moviesType.Id, Name = "New Movie", Year = 2024, HierarchyLevel = 0,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                };
                var midMovie = new MediaItem
                {
                    MediaTypeId = moviesType.Id, Name = "Mid Movie", Year = 2010, HierarchyLevel = 0,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                };
                var noYearMovie = new MediaItem
                {
                    MediaTypeId = moviesType.Id, Name = "Unknown Year Movie", Year = null, HierarchyLevel = 0,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                };
                db.MediaItems.AddRange(person, oldMovie, newMovie, midMovie, noYearMovie);
                await db.SaveChangesAsync();

                // Added deliberately out of year order, to prove the endpoint -- not insertion
                // order -- is what determines the response order.
                db.MediaCredits.AddRange(
                    new MediaCredit { MediaItemId = oldMovie.Id, PersonMediaItemId = person.Id, PersonName = "Test Actor", Role = "Actor", Source = "test" },
                    new MediaCredit { MediaItemId = noYearMovie.Id, PersonMediaItemId = person.Id, PersonName = "Test Actor", Role = "Actor", Source = "test" },
                    new MediaCredit { MediaItemId = newMovie.Id, PersonMediaItemId = person.Id, PersonName = "Test Actor", Role = "Actor", Source = "test" },
                    new MediaCredit { MediaItemId = midMovie.Id, PersonMediaItemId = person.Id, PersonName = "Test Actor", Role = "Actor", Source = "test" }
                );
                await db.SaveChangesAsync();

                var client = await AuthedClientAsync();
                var resp = await client.GetAsync($"/api/v1/people/{person.Id}/credits");
                resp.EnsureSuccessStatusCode();

                var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var actorGroup = body.RootElement.GetProperty("data").EnumerateArray()
                    .First(g => g.GetProperty("role").GetString() == "Actor");
                var names = actorGroup.GetProperty("items").EnumerateArray()
                    .Select(i => i.GetProperty("name").GetString())
                    .ToList();

                names.Should().Equal("New Movie", "Mid Movie", "Old Movie", "Unknown Year Movie");
            }
        }

        // Per-user request (2026-08-31): "delete the sorting and keep it alphabetical by
        // last name." PeopleController.GetPeople no longer takes a sort parameter -- it's
        // always ordered by a last-name-first key (PersonNameHelper), which jumpTo also
        // compares against.
        [Fact]
        public async Task GetPeople_OrdersByLastName_NotByFullNameOrFirstName()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            var peopleType = await db.MediaTypes.FirstOrDefaultAsync(t => t.Name == "people")
                ?? (await db.MediaTypes.AddAsync(new MediaType
                {
                    Name = "people", DisplayName = "People", HierarchyLevels = 1,
                    InteractionVerb = "viewed", ProgressUnit = "percent",
                    IsBuiltIn = true, IsActive = true, CreatedAt = DateTime.UtcNow,
                })).Entity;
            await db.SaveChangesAsync();

            // First names deliberately in the OPPOSITE order of their last names, so a
            // last-name sort and a full-name/first-name sort disagree on ordering -- proves
            // which key the endpoint actually uses.
            var zoeAnders = new MediaItem
            {
                MediaTypeId = peopleType.Id, Name = "Zoe Anders", HierarchyLevel = 0,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            var aliceZephyr = new MediaItem
            {
                MediaTypeId = peopleType.Id, Name = "Alice Zephyr", HierarchyLevel = 0,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.MediaItems.AddRange(zoeAnders, aliceZephyr);
            await db.SaveChangesAsync();

            var client = await AuthedClientAsync();
            var resp = await client.GetAsync("/api/v1/people?perPage=200");
            resp.EnsureSuccessStatusCode();

            var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var names = body.RootElement.GetProperty("data").EnumerateArray()
                .Select(i => i.GetProperty("name").GetString())
                .ToList();

            // "Zoe Anders" (last name Anders) must sort before "Alice Zephyr" (last name
            // Zephyr) -- the reverse of both full-name order ("Alice" < "Zoe") and any
            // by-first-name order.
            names.IndexOf("Zoe Anders").Should().BeLessThan(names.IndexOf("Alice Zephyr"));
        }

        [Fact]
        public async Task GetPeople_JumpTo_MatchesAgainstLastName()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            var peopleType = await db.MediaTypes.FirstOrDefaultAsync(t => t.Name == "people")
                ?? (await db.MediaTypes.AddAsync(new MediaType
                {
                    Name = "people", DisplayName = "People", HierarchyLevels = 1,
                    InteractionVerb = "viewed", ProgressUnit = "percent",
                    IsBuiltIn = true, IsActive = true, CreatedAt = DateTime.UtcNow,
                })).Entity;
            await db.SaveChangesAsync();

            var zoeAnders = new MediaItem
            {
                MediaTypeId = peopleType.Id, Name = "Zoe Anders", HierarchyLevel = 0,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            var aliceZephyr = new MediaItem
            {
                MediaTypeId = peopleType.Id, Name = "Alice Zephyr", HierarchyLevel = 0,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.MediaItems.AddRange(zoeAnders, aliceZephyr);
            await db.SaveChangesAsync();

            var client = await AuthedClientAsync();
            // Jumping to "Z" should land on the last-name-Z person (Alice Zephyr), not the
            // first-name-Z one (Zoe Anders, last name Anders, which sorts before "Z").
            var resp = await client.GetAsync("/api/v1/people?perPage=200&jumpTo=Z");
            resp.EnsureSuccessStatusCode();

            var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var names = body.RootElement.GetProperty("data").EnumerateArray()
                .Select(i => i.GetProperty("name").GetString())
                .ToList();

            names.Should().Contain("Alice Zephyr");
            names.Should().NotContain("Zoe Anders");
        }
    }
}
