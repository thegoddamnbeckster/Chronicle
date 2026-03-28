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
        var sut = new PluginTaskRunner(enrichment.Object);

        await sut.RunAsync("chronicle.plugin.musicbrainz", "fetch-missing-metadata",
                           CancellationToken.None);

        enrichment.Verify(e => e.EnrichPendingAsync(
            "chronicle.plugin.musicbrainz", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_ResyncAllMetadata_CallsResyncAllForPlugin()
    {
        var enrichment = new Mock<IMetadataEnrichmentService>();
        var sut = new PluginTaskRunner(enrichment.Object);

        await sut.RunAsync("chronicle.plugin.musicbrainz", "resync-all-metadata",
                           CancellationToken.None);

        enrichment.Verify(e => e.ResyncAllForPluginAsync(
            "chronicle.plugin.musicbrainz", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_UnknownTaskId_DoesNotThrow()
    {
        var sut = new PluginTaskRunner(Mock.Of<IMetadataEnrichmentService>());

        // Should not throw
        await sut.RunAsync("some.plugin", "unknown-task-id", CancellationToken.None);
    }
}
