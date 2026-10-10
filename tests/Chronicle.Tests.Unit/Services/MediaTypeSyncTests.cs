using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace Chronicle.Tests.Unit.Services;

/// <summary>How installed plugins create and describe media types, and how an administrator's edits are protected from that.</summary>
public class MediaTypeSyncTests : IDisposable
{
    private readonly ChronicleDbContext _db;
    private readonly Mock<IPluginRegistry> _registry = new();

    public MediaTypeSyncTests()
    {
        _db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _registry.Setup(r => r.GetFileScannerPlugins()).Returns([]);
    }

    public void Dispose() => _db.Dispose();

    private PluginHostService Service(params MediaTypeSupport[] declared)
    {
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.GetSupportedMediaTypes()).Returns(declared);
        _registry.Setup(r => r.GetMetadataProviders()).Returns([provider.Object]);
        return new PluginHostService(Mock.Of<IServiceScopeFactory>(), _registry.Object, Mock.Of<IPluginSettingsProtector>(), Mock.Of<IHostEnvironment>());
    }

    private static MediaTypeSupport Declares(string name, string display, int levels = 1, string[]? labels = null,
        string verb = "watched", string? scan = null) =>
        new() { MediaTypeName = name, DisplayName = display, HierarchyLevels = levels, HierarchyLabels = labels, InteractionVerb = verb, ScanStrategy = scan };

    [Fact]
    public async Task ANewType_IsCreatedFromWhatThePluginDeclares()
    {
        await Service(Declares("comics", "Comics", 3, ["Series", "Volume", "Issue"], "read")).SyncMediaTypesFromPluginsAsync(_db, default);

        var t = _db.MediaTypes.Single();
        t.Name.Should().Be("comics");
        t.DisplayName.Should().Be("Comics");
        t.HierarchyLevels.Should().Be(3);
        t.HierarchyLabels.Should().Be("Series,Volume,Issue");
        t.InteractionVerb.Should().Be("read");
        t.IsBuiltIn.Should().BeFalse();
        t.IsUserModified.Should().BeFalse("a plugin made it, so a plugin may keep describing it");
    }

    [Fact]
    public async Task AnAudiobooksTypeCreatedByAPlugin_GetsTheAudiobookScanStrategyAsData_WhenThePluginDoesNotSay()
    {
        await Service(Declares("audiobooks", "Audiobooks", 3, ["Author", "Series", "Book"], "listened")).SyncMediaTypesFromPluginsAsync(_db, default);

        _db.MediaTypes.Single().ScanStrategy.Should().Be("audiobook");
    }

    [Fact]
    public async Task APluginMayStateTheScanStrategyItself()
    {
        await Service(Declares("spoken-word", "Spoken Word", 3, scan: "audiobook")).SyncMediaTypesFromPluginsAsync(_db, default);

        _db.MediaTypes.Single().ScanStrategy.Should().Be("audiobook");
    }

    [Fact]
    public async Task AnUnknownStrategyFromAPlugin_IsNotStored()
    {
        await Service(Declares("things", "Things", scan: "made-up")).SyncMediaTypesFromPluginsAsync(_db, default);

        _db.MediaTypes.Single().ScanStrategy.Should().BeNull();
    }

    [Fact]
    public async Task AnOrdinaryPluginManagedType_FollowsThePluginWhenItChanges()
    {
        _db.MediaTypes.Add(new MediaType { Name = "comics", DisplayName = "Comics", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        await Service(Declares("comics", "Comic Books", 3, ["Series", "Volume", "Issue"], "read")).SyncMediaTypesFromPluginsAsync(_db, default);

        var t = _db.MediaTypes.Single();
        t.DisplayName.Should().Be("Comic Books");
        t.HierarchyLevels.Should().Be(3);
        t.InteractionVerb.Should().Be("read");
    }

    [Fact]
    public async Task ATypeAnAdministratorEdited_IsLeftExactlyAsTheyMadeIt()
    {
        _db.MediaTypes.Add(new MediaType
        {
            Name = "comics", DisplayName = "My Comics", HierarchyLevels = 2, HierarchyLabels = "Series,Issue",
            InteractionVerb = "enjoyed", IsUserModified = true, CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await Service(Declares("comics", "Comic Books", 3, ["Series", "Volume", "Issue"], "read")).SyncMediaTypesFromPluginsAsync(_db, default);

        var t = _db.MediaTypes.Single();
        t.DisplayName.Should().Be("My Comics");
        t.HierarchyLevels.Should().Be(2);
        t.HierarchyLabels.Should().Be("Series,Issue");
        t.InteractionVerb.Should().Be("enjoyed");
    }

    [Fact]
    public async Task ReleasingAType_LetsThePluginDescribeItAgain()
    {
        _db.MediaTypes.Add(new MediaType { Name = "comics", DisplayName = "My Comics", IsUserModified = true, CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        _db.MediaTypes.Single().IsUserModified = false;
        await _db.SaveChangesAsync();

        await Service(Declares("comics", "Comic Books")).SyncMediaTypesFromPluginsAsync(_db, default);

        _db.MediaTypes.Single().DisplayName.Should().Be("Comic Books");
    }

    [Fact]
    public async Task APluginNeverChangesAnExistingScanStrategy()
    {
        _db.MediaTypes.Add(new MediaType { Name = "audiobooks", DisplayName = "Audiobooks", ScanStrategy = null, CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        await Service(Declares("audiobooks", "Audiobooks", scan: "audiobook")).SyncMediaTypesFromPluginsAsync(_db, default);

        _db.MediaTypes.Single().ScanStrategy.Should().BeNull("the value in the database is the truth once the type exists");
    }

    [Fact]
    public async Task AliasEntriesWithoutADisplayName_AreNotTurnedIntoTypes()
    {
        await Service(new MediaTypeSupport { MediaTypeName = "movie" }, Declares("movies", "Movies")).SyncMediaTypesFromPluginsAsync(_db, default);

        _db.MediaTypes.Select(t => t.Name).Should().Equal("movies");
    }
}
