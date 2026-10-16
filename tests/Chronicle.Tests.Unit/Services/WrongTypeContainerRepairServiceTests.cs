using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronicle.Tests.Unit.Services;

public class WrongTypeContainerRepairServiceTests
{
    private const int PeopleType = 1;
    private const int AudiobookType = 2;
    private const int MusicType = 3;

    private sealed class Rig : IDisposable
    {
        public ChronicleDbContext Db { get; } = new(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        private readonly ServiceProvider _provider;
        public WrongTypeContainerRepairService Service { get; }

        public Rig()
        {
            Db.MediaTypes.AddRange(
                new MediaType { Id = PeopleType, Name = "people", IsPersonType = true, DisplayName = "People", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow },
                new MediaType { Id = AudiobookType, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow },
                new MediaType { Id = MusicType, Name = "music", DisplayName = "Music", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
            Db.SaveChanges();
            var services = new ServiceCollection();
            services.AddSingleton(Db);
            _provider = services.BuildServiceProvider();
            Service = new WrongTypeContainerRepairService(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<WrongTypeContainerRepairService>.Instance);
        }

        public MediaItem Add(int typeId, string name, int level = 0, int? parentId = null)
        {
            var item = new MediaItem { MediaTypeId = typeId, Name = name, HierarchyLevel = level, ParentId = parentId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.MediaItems.Add(item);
            Db.SaveChanges();
            return item;
        }

        public void Dispose() { _provider.Dispose(); Db.Dispose(); }
    }

    [Fact]
    public async Task APersonWhoseChildrenAreAllAudiobooks_BecomesAnAudiobookItem()
    {
        using var rig = new Rig();
        var stub = rig.Add(PeopleType, "Iain M. Banks");
        rig.Add(AudiobookType, "Culture", 1, stub.Id);
        rig.Add(AudiobookType, "Transition", 1, stub.Id);

        await rig.Service.ExecuteAsync(CancellationToken.None);

        (await rig.Db.MediaItems.FindAsync(stub.Id))!.MediaTypeId.Should().Be(AudiobookType);
    }

    [Fact]
    public async Task APersonWithCredits_StaysAPerson()
    {
        using var rig = new Rig();
        var person = rig.Add(PeopleType, "Iain M. Banks");
        var film = rig.Add(AudiobookType, "Some Film");
        rig.Add(AudiobookType, "Culture", 1, person.Id);
        rig.Db.MediaCredits.Add(new MediaCredit { MediaItemId = film.Id, PersonMediaItemId = person.Id, Role = "Writer" });
        rig.Db.SaveChanges();

        await rig.Service.ExecuteAsync(CancellationToken.None);

        (await rig.Db.MediaItems.FindAsync(person.Id))!.MediaTypeId.Should().Be(PeopleType);
    }

    [Fact]
    public async Task ChildrenOfSeveralTypes_AreNotGuessedAt()
    {
        using var rig = new Rig();
        var person = rig.Add(PeopleType, "Mixed");
        rig.Add(AudiobookType, "A", 1, person.Id);
        rig.Add(MusicType, "B", 1, person.Id);

        await rig.Service.ExecuteAsync(CancellationToken.None);

        (await rig.Db.MediaItems.FindAsync(person.Id))!.MediaTypeId.Should().Be(PeopleType);
    }

    [Fact]
    public async Task AnOrdinaryItemWithChildrenOfItsOwnType_IsUntouched()
    {
        using var rig = new Rig();
        var author = rig.Add(AudiobookType, "Author");
        rig.Add(AudiobookType, "Series", 1, author.Id);

        await rig.Service.ExecuteAsync(CancellationToken.None);

        (await rig.Db.MediaItems.FindAsync(author.Id))!.MediaTypeId.Should().Be(AudiobookType);
    }
}
