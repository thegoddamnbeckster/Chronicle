using Chronicle.Plugins;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class PluginTaskRunnerTests
{
    [Fact]
    public async Task RunAsync_FetchMissingMetadata_CallsEnrichPendingForPlugin()
    {
        var enrichment = new Mock<IMetadataEnrichmentService>();
        var sut = new PluginTaskRunner(
            enrichment.Object, Mock.Of<ISyncOrchestrationService>(), Mock.Of<IPluginRegistry>());

        await sut.RunAsync("chronicle.plugin.musicbrainz", "fetch-missing-metadata",
                           CancellationToken.None);

        enrichment.Verify(e => e.EnrichPendingAsync(
            "chronicle.plugin.musicbrainz", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_ResyncAllMetadata_CallsResyncAllForPlugin()
    {
        var enrichment = new Mock<IMetadataEnrichmentService>();
        var sut = new PluginTaskRunner(
            enrichment.Object, Mock.Of<ISyncOrchestrationService>(), Mock.Of<IPluginRegistry>());

        await sut.RunAsync("chronicle.plugin.musicbrainz", "resync-all-metadata",
                           CancellationToken.None);

        enrichment.Verify(e => e.ResyncAllForPluginAsync(
            "chronicle.plugin.musicbrainz", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_UnknownTaskId_NoMatchingIPluginTask_DoesNotThrow()
    {
        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetPluginTask("some.plugin", "unknown-task-id")).Returns((IPluginTask?)null);
        var sut = new PluginTaskRunner(
            Mock.Of<IMetadataEnrichmentService>(), Mock.Of<ISyncOrchestrationService>(), registry.Object);

        // Should not throw
        await sut.RunAsync("some.plugin", "unknown-task-id", CancellationToken.None);
    }

    [Fact]
    public async Task RunAsync_CustomTaskId_DispatchesToMatchingIPluginTask()
    {
        // Closes the gap PluginHostService/PluginTaskRunner used to leave: a custom task_id
        // declared in a plugin's manifest.json with a matching IPluginTask implementation must
        // actually run, not just log a warning (see PluginRegistry.GetPluginTask).
        var customTask = new Mock<IPluginTask>();
        customTask.Setup(t => t.TaskId).Returns("sync-search-index");
        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetPluginTask("chronicle.plugin.moviesremastered", "sync-search-index"))
            .Returns(customTask.Object);
        var sut = new PluginTaskRunner(
            Mock.Of<IMetadataEnrichmentService>(), Mock.Of<ISyncOrchestrationService>(), registry.Object);

        await sut.RunAsync("chronicle.plugin.moviesremastered", "sync-search-index", CancellationToken.None);

        customTask.Verify(t => t.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
