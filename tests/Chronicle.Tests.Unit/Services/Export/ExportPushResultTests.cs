using Chronicle.Plugins;
using Xunit;

namespace Chronicle.Tests.Unit.Services.Export;

public class ExportPushResultTests
{
    [Fact]
    public void ThePreSkippedConstructorStillExists_SoAnOlderPluginKeepsWorking()
    {
        // A plugin compiled against the earlier shape calls exactly this six-argument constructor.
        var ctor = typeof(ExportPushResult).GetConstructor([typeof(int), typeof(int), typeof(int), typeof(int), typeof(IReadOnlyList<string>), typeof(int)]);

        Assert.NotNull(ctor);
        var result = (ExportPushResult)ctor!.Invoke([1, 2, 3, 4, new List<string>(), 5]);
        Assert.Equal(5, result.Deferred);
        Assert.Equal(0, result.Skipped);
    }
}
