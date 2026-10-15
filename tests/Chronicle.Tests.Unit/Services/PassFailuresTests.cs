using Chronicle.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class PassFailuresTests
{
    [Fact]
    public async Task AFailingPass_DoesNotStopTheNext_ButTheTaskStillFails()
    {
        var passes = new PassFailures(NullLogger.Instance, "Test task");
        var ran = new List<string>();

        await passes.RunAsync("one", _ => { ran.Add("one"); throw new InvalidOperationException("boom"); }, default);
        await passes.RunAsync("two", _ => { ran.Add("two"); return Task.CompletedTask; }, default);

        ran.Should().Equal("one", "two");
        var act = passes.ThrowIfAny;
        act.Should().Throw<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task SeveralFailures_AreReportedTogether()
    {
        var passes = new PassFailures(NullLogger.Instance, "Test task");
        await passes.RunAsync("one", _ => throw new InvalidOperationException("a"), default);
        await passes.RunAsync("two", _ => throw new InvalidOperationException("b"), default);

        var act = passes.ThrowIfAny;
        act.Should().Throw<AggregateException>().Which.InnerExceptions.Should().HaveCount(2);
    }

    [Fact]
    public async Task NoFailure_DoesNotThrow()
    {
        var passes = new PassFailures(NullLogger.Instance, "Test task");
        await passes.RunAsync("one", _ => Task.CompletedTask, default);
        passes.ThrowIfAny();
    }

    [Fact]
    public async Task ACancellation_IsNotSwallowed()
    {
        var passes = new PassFailures(NullLogger.Instance, "Test task");
        var act = () => passes.RunAsync("one", _ => throw new OperationCanceledException(), default);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
