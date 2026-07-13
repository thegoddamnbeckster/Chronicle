using Chronicle.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Chronicle.Tests.Unit.Services;

public class TagMismatchRematchTaskTests
{
    private static (TagMismatchRematchTask task, TagMismatchRematchQueue queue, Mock<IMetadataEnrichmentService> enrichment)
        Build()
    {
        var queue = new TagMismatchRematchQueue();
        var enrichmentMock = new Mock<IMetadataEnrichmentService>();

        var services = new ServiceCollection();
        services.AddSingleton(enrichmentMock.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var task = new TagMismatchRematchTask(queue, scopeFactory, NullLogger<TagMismatchRematchTask>.Instance);
        return (task, queue, enrichmentMock);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyQueue_DoesNothing()
    {
        var (task, _, enrichment) = Build();

        await task.ExecuteAsync(CancellationToken.None);

        enrichment.Verify(e => e.EnrichItemAsync(
            It.IsAny<int>(), It.IsAny<EnrichmentOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_QueuedItems_CallsForceEnrichForEach()
    {
        var (task, queue, enrichment) = Build();
        queue.TryEnqueue(42);
        queue.TryEnqueue(43);

        await task.ExecuteAsync(CancellationToken.None);

        enrichment.Verify(e => e.EnrichItemAsync(
            42, It.Is<EnrichmentOptions>(o => o.Mode == EnrichmentMode.Force && o.Cascade), It.IsAny<CancellationToken>()),
            Times.Once);
        enrichment.Verify(e => e.EnrichItemAsync(
            43, It.Is<EnrichmentOptions>(o => o.Mode == EnrichmentMode.Force && o.Cascade), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ItemAlreadyProcessed_CanBeReQueuedAfterwards()
    {
        var (task, queue, _) = Build();
        queue.TryEnqueue(42);
        await task.ExecuteAsync(CancellationToken.None);

        // Once drained+processed, the same item should be enqueueable again (not stuck as "pending" forever).
        queue.TryEnqueue(42).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_OneItemThrows_OthersStillProcessed()
    {
        var (task, queue, enrichment) = Build();
        queue.TryEnqueue(42);
        queue.TryEnqueue(43);

        enrichment.Setup(e => e.EnrichItemAsync(42, It.IsAny<EnrichmentOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("plugin unavailable"));

        var act = async () => await task.ExecuteAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        enrichment.Verify(e => e.EnrichItemAsync(
            43, It.IsAny<EnrichmentOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
