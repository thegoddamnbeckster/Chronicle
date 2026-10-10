using Chronicle.Services.Notifications;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class TaskFailureTriageTests
{
    public static TheoryData<Exception, TaskFailureKind> Cases => new()
    {
        { new HttpRequestException("503"), TaskFailureKind.Fixable },
        { new IOException("There is not enough space on the disk."), TaskFailureKind.Fixable },
        { new UnauthorizedAccessException("denied"), TaskFailureKind.Fixable },
        { new TaskCanceledException("timeout"), TaskFailureKind.Fixable },
        { new InvalidOperationException("The plugin is not configured."), TaskFailureKind.Fixable },
        { new NullReferenceException(), TaskFailureKind.Internal },
        { new InvalidCastException(), TaskFailureKind.Internal },
        { new KeyNotFoundException(), TaskFailureKind.Internal },
        { new IndexOutOfRangeException(), TaskFailureKind.Internal },
        { new MissingMethodException("Method not found"), TaskFailureKind.PluginIncompatible },
        { new MissingFieldException("Field not found"), TaskFailureKind.PluginIncompatible },
        { new TypeLoadException("Could not load type"), TaskFailureKind.PluginIncompatible },
        { new BadImageFormatException(), TaskFailureKind.PluginIncompatible },
        { new FileLoadException("Could not load file or assembly"), TaskFailureKind.PluginIncompatible },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void SortsAnExceptionByWhatSomeoneCouldDoAboutIt(Exception ex, TaskFailureKind expected) =>
        TaskFailureTriage.Classify(ex).Should().Be(expected);

    [Fact]
    public void AVersionMismatchInsideAWrapper_IsStillAVersionMismatch() =>
        TaskFailureTriage.Classify(new InvalidOperationException("sync failed", new MissingMethodException("x")))
            .Should().Be(TaskFailureKind.PluginIncompatible);

    [Fact]
    public void ABugInsideAGenericWrapper_IsStillABug() =>
        TaskFailureTriage.Classify(new InvalidOperationException("sync failed", new NullReferenceException()))
            .Should().Be(TaskFailureKind.Internal);

    [Fact]
    public void ANetworkErrorInsideAWrapper_IsStillANetworkError() =>
        TaskFailureTriage.Classify(new InvalidOperationException("sync failed", new HttpRequestException("503")))
            .Should().Be(TaskFailureKind.Fixable);

    [Fact]
    public void AggregatesAreLookedInto() =>
        TaskFailureTriage.Classify(new AggregateException(new HttpRequestException("503"), new MissingMethodException("x")))
            .Should().Be(TaskFailureKind.PluginIncompatible);

    [Fact]
    public void NoExceptionAtAll_IsTreatedAsFixable_SoNothingIsHiddenByAccident() =>
        TaskFailureTriage.Classify(null).Should().Be(TaskFailureKind.Fixable);
}
