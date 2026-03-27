using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Plugins;

public class IMetadataProviderContractTests
{
    [Fact]
    public void SearchAsync_AcceptsMediaSearchContext()
    {
        var mock = new Mock<IMetadataProvider>();
        var ctx = new MediaSearchContext("test", 2001);
        // Compiles only if IMetadataProvider.SearchAsync takes MediaSearchContext
        mock.Setup(p => p.SearchAsync(ctx, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScoredCandidate>());
        Assert.True(true);
    }
}
