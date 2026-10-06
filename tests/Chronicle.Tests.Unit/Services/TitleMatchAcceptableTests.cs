using Chronicle.Plugins.Models;
using Chronicle.Services;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// The name check a provider's candidate must pass for a file-scanner item. Real misses (2026-10-05): the
/// provider's own exact alternate-title match, and names differing only in punctuation, diacritics, word
/// breaks or a leading article, were all turned away -- while a fan edit's subtitle must still be.
/// </summary>
public class TitleMatchAcceptableTests
{
    [Theory]
    [InlineData("Dam Sharks!", "Dam Sharks")]
    [InlineData("Cowboys vs. Dinosaurs", "Cowboys vs Dinosaurs")]
    [InlineData("Mārama", "Marama")]
    [InlineData("Stopmotion", "Stop-Motion")]
    [InlineData("Mar.IA", "Maria")]
    [InlineData("The Flu", "Flu")]
    [InlineData("Flu", "The Flu")]
    [InlineData("Rick's Story", "Ricks Story")]
    [InlineData("Rick’s Story", "Ricks Story")]
    [InlineData("Alien - Darksteel Cut (2023)", "Alien: Darksteel Cut")]
    public void Accepts_NamesThatDifferOnlyInPunctuationAccentsWordBreaksOrAnArticle(string item, string title) =>
        EnrichmentNameCheck(item, title).Should().BeTrue();

    [Theory]
    [InlineData("Alien: Darksteel Cut", "Alien")]                       // a fan edit is not the original film
    [InlineData("Apocalyptic", "Apocalypse Cult")]
    [InlineData("Big", "Bigger")]
    [InlineData("Season 2", "Open Season 3")]
    public void StillRejects_NamesThatReallyDiffer(string item, string title) =>
        EnrichmentNameCheck(item, title).Should().BeFalse();

    [Fact]
    public void ACandidateKnownByTheItemsNameThroughAnAlternateTitle_IsAccepted()
    {
        var candidate = new MediaMetadata { Title = "Saltwater", AlternateNames = ["Atomic Shark"] };

        MetadataEnrichmentService.IsCandidateNameAcceptable("Atomic Shark", candidate).Should().BeTrue();
        MetadataEnrichmentService.IsCandidateNameAcceptable("Something Else Entirely", candidate).Should().BeFalse();
    }

    private static bool EnrichmentNameCheck(string item, string title) =>
        MetadataEnrichmentService.IsTitleMatchAcceptable(item, title);
}
