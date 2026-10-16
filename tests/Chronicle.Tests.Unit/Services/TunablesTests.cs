using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class TunablesTests
{
    public TunablesTests() => Tunables.LoadForCurrentContext([]);

    [Fact]
    public void WithNothingStored_EveryNumberIsItsDefault()
    {
        foreach (var d in Tunables.All) Tunables.Value(d).Should().Be(d.Default, d.Key);
    }

    [Fact]
    public void AStoredNumber_ReplacesTheDefault()
    {
        Tunables.LoadForCurrentContext([new(Tunables.WatchedPercent.Key, "90")]);

        Tunables.Value(Tunables.WatchedPercent).Should().Be(90);
    }

    [Theory]
    [InlineData("not a number")]
    [InlineData("")]
    [InlineData("0")]       // below the minimum: never switches a rule off by accident
    [InlineData("101")]
    [InlineData("NaN")]
    public void AnUnusableStoredValue_IsIgnored(string stored)
    {
        Tunables.LoadForCurrentContext([new(Tunables.WatchedPercent.Key, stored)]);

        Tunables.Value(Tunables.WatchedPercent).Should().Be(80);
    }

    [Fact]
    public void ValuesAreReadInTheirUnit()
    {
        Tunables.LoadForCurrentContext([new(Tunables.EnrichmentRetryHours.Key, "6"), new(Tunables.TypeMismatchMajorityPercent.Key, "75")]);

        Tunables.Hours(Tunables.EnrichmentRetryHours).Should().Be(TimeSpan.FromHours(6));
        Tunables.Fraction(Tunables.TypeMismatchMajorityPercent).Should().Be(0.75);
        Tunables.Days(Tunables.SeriesRecheckDays).Should().Be(TimeSpan.FromDays(7));
    }

    [Fact]
    public void OnlyRowsUnderThePrefixAreRead()
    {
        Tunables.LoadForCurrentContext([new("auth.something", "1"), new(Tunables.WatchedPercent.Key, "70")]);

        Tunables.Value(Tunables.WatchedPercent).Should().Be(70);
    }

    [Fact]
    public void EveryKeyIsUnique_UnderThePrefix_WithADefaultInsideItsRange()
    {
        Tunables.All.Select(d => d.Key).Should().OnlyHaveUniqueItems().And.OnlyContain(k => k.StartsWith(Tunables.Prefix));
        Tunables.All.Should().OnlyContain(d => d.Default >= d.Min && d.Default <= d.Max && d.Min < d.Max);
    }

    [Fact]
    public void AnAdministratorsMismatchShare_ChangesTheDetectorsThreshold()
    {
        Tunables.LoadForCurrentContext([new(Tunables.TypeMismatchMajorityPercent.Key, "80")]);

        Chronicle.Services.Scan.MediaTypeMismatchDetector.MajorityShare.Should().Be(0.8);
    }
}
