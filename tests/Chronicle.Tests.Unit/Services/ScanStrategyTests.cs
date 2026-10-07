using Chronicle.Core.Models;
using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

/// <summary>The scanner chooses audiobook handling from a property of the type, never from its name.</summary>
public class ScanStrategyTests
{
    [Fact]
    public void ATypeWithTheAudiobookStrategy_IsScannedAsAudiobooks_WhateverItIsCalled()
    {
        FileScanService.IsAudiobookScan(new MediaType { Name = "spoken-word", ScanStrategy = ScanStrategies.Audiobook }).Should().BeTrue();
        FileScanService.IsAudiobookScan(new MediaType { Name = "audiobooks", ScanStrategy = ScanStrategies.Audiobook }).Should().BeTrue();
    }

    [Fact]
    public void ATypeCalledAudiobooks_WithoutTheStrategy_IsNotTreatedSpecially()
    {
        FileScanService.IsAudiobookScan(new MediaType { Name = "audiobooks", ScanStrategy = null }).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Audiobook")]   // the stored value is exact; the page only offers the real one
    [InlineData("other")]
    public void AnythingElse_IsAnOrdinaryScan(string? strategy)
    {
        FileScanService.IsAudiobookScan(new MediaType { Name = "movies", ScanStrategy = strategy }).Should().BeFalse();
    }
}
