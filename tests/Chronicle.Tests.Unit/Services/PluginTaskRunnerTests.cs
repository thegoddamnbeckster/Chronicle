using Chronicle.Services;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class PluginTaskRunnerTests
{
    [Fact]
    public async Task RunAsync_FetchMissingMetadata_CallsEnrichPendingForPlugin()
    {
        var enrichment = new Mock<IMetadataEnrichmentService>();
        var refresh    = new Mock<IMetadataRefreshService>();
        var sut = new PluginTaskRunner(enrichment.Object, refresh.Object);

        await sut.RunAsync("chronicle.plugin.musicbrainz", "fetch-missing-metadata",
                           CancellationToken.None);

        enrichment.Verify(e => e.EnrichPendingAsync(
            "chronicle.plugin.musicbrainz", It.IsAny<CancellationToken>()), Times.Once);
        refresh.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_ResyncAllMetadata_CallsRefreshForPlugin()
    {
        var enrichment = new Mock<IMetadataEnrichmentService>();
        var refresh    = new Mock<IMetadataRefreshService>();
        var sut = new PluginTaskRunner(enrichment.Object, refresh.Object);

        await sut.RunAsync("chronicle.plugin.musicbrainz", "resync-all-metadata",
                           CancellationToken.None);

        refresh.Verify(r => r.RefreshForPluginAsync(
            "chronicle.plugin.musicbrainz", It.IsAny<CancellationToken>()), Times.Once);
        enrichment.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_UnknownTaskId_DoesNotThrow()
    {
        var sut = new PluginTaskRunner(Mock.Of<IMetadataEnrichmentService>(),
                                       Mock.Of<IMetadataRefreshService>());

        // Should not throw
        await sut.RunAsync("some.plugin", "unknown-task-id", CancellationToken.None);
    }
}
