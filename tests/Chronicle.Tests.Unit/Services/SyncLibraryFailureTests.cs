using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// A library that cannot be read (a wrong account, a rejected key, a private profile) must fail the import where the owner can see it.
/// It used to be logged and the task reported "completed successfully" with nothing imported.
/// </summary>
public class SyncLibraryFailureTests
{
    private static (SyncOrchestrationService Svc, string Db) Build(Mock<IImportProvider> provider)
    {
        var name = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ChronicleDbContext>(o => o.UseInMemoryDatabase(name));
        var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            db.Users.Add(new User { Id = 1, Username = "u", Email = "u@x", PasswordHash = "x" });
            db.SaveChanges();
        }
        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetImportProvider("chronicle.plugin.steam")).Returns(provider.Object);
        registry.Setup(r => r.GetMetadataProviderEntries()).Returns([]);
        var svc = new SyncOrchestrationService(sp.GetRequiredService<IServiceScopeFactory>(), registry.Object,
            Mock.Of<IMetadataResolutionService>(), NullLogger<SyncOrchestrationService>.Instance, Mock.Of<IHostApplicationLifetime>());
        return (svc, name);
    }

    private static Mock<IImportProvider> Provider()
    {
        var p = new Mock<IImportProvider>();
        p.SetupGet(x => x.Name).Returns("Steam");
        p.Setup(x => x.IsAuthenticatedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        p.Setup(x => x.GetCapabilities()).Returns(new ImportCapabilities(false, false, false, false) { SupportsLibrary = true });
        p.Setup(x => x.GetWatchHistoryAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        p.Setup(x => x.GetRatingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        p.Setup(x => x.GetWatchlistAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        p.Setup(x => x.GetPlaybackProgressAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return p;
    }

    [Fact]
    public async Task A_library_that_cannot_be_read_fails_the_import_with_the_reason()
    {
        var p = Provider();
        p.Setup(x => x.GetLibraryAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PluginAuthException("chronicle.plugin.steam", "That is not a Steam account Steam can find."));
        var (svc, _) = Build(p);

        var act = () => svc.SyncAsync("chronicle.plugin.steam", fullSync: true, userId: 1);

        (await act.Should().ThrowAsync<PluginAuthException>()).WithMessage("*not a Steam account*");
    }

    [Fact]
    public async Task A_library_that_reads_fine_does_not_fail_and_an_empty_one_is_not_an_error()
    {
        var p = Provider();
        p.Setup(x => x.GetLibraryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var (svc, _) = Build(p);

        var summary = await svc.SyncAsync("chronicle.plugin.steam", fullSync: true, userId: 1);

        summary.Errors.Should().BeEmpty();
    }
}
