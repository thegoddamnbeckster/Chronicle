using Chronicle.Services.Scan;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class TrackFileNameTests
{
    [Theory]
    [InlineData("01 Enter Sandman", null, 1, null, "Enter Sandman")]
    [InlineData("01 - Enter Sandman", null, 1, null, "Enter Sandman")]
    [InlineData("01. Enter Sandman", null, 1, null, "Enter Sandman")]
    [InlineData("1 - Enter Sandman", null, 1, null, "Enter Sandman")]
    [InlineData("1. Enter Sandman", null, 1, null, "Enter Sandman")]
    [InlineData("12)Enter Sandman", null, 12, null, "Enter Sandman")]
    [InlineData("007 Bond Theme", null, 7, null, "Bond Theme")]
    [InlineData("1-02 Sad But True", 1, 2, null, "Sad But True")]
    [InlineData("2.05 - Of Wolf and Man", 2, 5, null, "Of Wolf and Man")]
    [InlineData("Track 05 - Holier Than Thou", null, 5, null, "Holier Than Thou")]
    [InlineData("track05 Holier Than Thou", null, 5, null, "Holier Than Thou")]
    [InlineData("Metallica - 03 - The Unforgiven", null, 3, "Metallica", "The Unforgiven")]
    public void ReadsTheNumbersOutOfTheName(string stem, int? disc, int? track, string? artist, string title)
    {
        var parts = TrackFileName.Parse(stem);

        parts.Disc.Should().Be(disc);
        parts.Track.Should().Be(track);
        parts.Artist.Should().Be(artist);
        parts.Title.Should().Be(title);
    }

    [Theory]
    [InlineData("99 Problems")]
    [InlineData("7 Years")]
    [InlineData("1999")]
    [InlineData("2 Minutes to Midnight")]
    [InlineData("Enter Sandman")]
    [InlineData("Artist - Song Title")]
    [InlineData("Song - Live")]
    [InlineData("Track")]
    [InlineData("Track 05")]
    [InlineData("")]
    [InlineData("   ")]
    public void LeavesTitlesThatMerelyStartWithANumberAlone(string stem)
    {
        var parts = TrackFileName.Parse(stem);

        parts.Track.Should().BeNull();
        parts.Disc.Should().BeNull();
        parts.Title.Should().Be(string.IsNullOrWhiteSpace(stem) ? stem : stem.Trim());
    }
}

public class MusicFolderSignalTests
{
    private readonly FolderSignalExtractor _extractor = new();

    [Fact]
    public void AnAudioFilesTitle_LosesItsTrackNumber()
    {
        var s = _extractor.Extract("C:/Music/Metallica/Black Album/01 - Enter Sandman.mp3", "C:/Music");

        s.DetectedTrackNumber.Should().Be(1);
        s.TrackTitle.Should().Be("Enter Sandman");
        s.FileName.Should().Be("01 - Enter Sandman");
    }

    [Fact]
    public void ADiscAndTrackInTheName_AreBothRead()
    {
        var s = _extractor.Extract("C:/Music/Pink Floyd/The Wall/2-05 Comfortably Numb.flac", "C:/Music");

        s.DetectedDiscNumber.Should().Be(2);
        s.DetectedTrackNumber.Should().Be(5);
        s.TrackTitle.Should().Be("Comfortably Numb");
    }

    [Fact]
    public void ADiscFolder_IsNotOverriddenByTheNameWhenThereIsNoDiscInIt()
    {
        var s = _extractor.Extract("C:/Music/Pink Floyd/The Wall/CD2/05 Comfortably Numb.flac", "C:/Music");

        s.DetectedDiscNumber.Should().Be(2);
        s.DetectedTrackNumber.Should().Be(5);
    }

    [Fact]
    public void ATvEpisodeWithANumberedName_KeepsItsName()
    {
        var s = _extractor.Extract("C:/TV/Star Trek/Season 1/01 - The Vulcan Hello.mkv", "C:/TV");

        s.TrackTitle.Should().BeNull();
        s.FileName.Should().Be("01 - The Vulcan Hello");
    }

    [Fact]
    public void AnAudioFileWithAnEpisodeCode_IsNotReinterpretedAsATrack()
    {
        var s = _extractor.Extract("C:/Pod/Show/S01E02 - 01 Intro.mp3", "C:/Pod");

        s.TrackTitle.Should().BeNull();
    }

    [Fact]
    public void ATitleThatStartsWithANumber_IsLeftWhole()
    {
        var s = _extractor.Extract("C:/Music/Artist/Album/99 Problems.mp3", "C:/Music");

        s.TrackTitle.Should().BeNull();
    }

    [Fact]
    public void Grouping_NamesUntaggedTracksWithoutTheirNumbers()
    {
        var svc = new ScanGroupingService(new FolderSignalExtractor(), new TagSignalExtractor());

        var result = svc.Group(["C:/Music/Metallica/Black Album/01 - Enter Sandman.mp3", "C:/Music/Metallica/Black Album/02 - Sad But True.mp3"], "C:/Music", 3);

        var tracks = result.Groups.Single().Children.Single().Children;
        tracks.Select(t => t.Name).Should().Equal("Enter Sandman", "Sad But True");
        tracks.Select(t => t.Number).Should().Equal(1, 2);
    }
}
