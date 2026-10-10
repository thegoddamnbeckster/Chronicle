using Chronicle.Core.Models;
using Chronicle.Core.Models.Scan;
using Chronicle.Data;
using Chronicle.Services;
using Chronicle.Services.Scan;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Tests.Unit.Services;

public class SidecarRulesTests
{
    [Fact]
    public void Defaults_WhenNothingIsConfigured()
    {
        var rules = SidecarRules.From(null);

        rules.Extensions.Should().Contain(".srt").And.Contain(".jpg");
        rules.Folders.Should().Contain("extras");
    }

    [Fact]
    public void ConfiguredLists_ReplaceTheDefaults_AndExtensionsGetTheirDot()
    {
        var rules = SidecarRules.From(new Dictionary<string, string>
        {
            [SidecarRules.ExtensionsKey] = "vtt, .pdf",
            [SidecarRules.FoldersKey] = "Bonus",
        });

        rules.Extensions.Should().BeEquivalentTo(".vtt", ".pdf");
        rules.Folders.Should().ContainSingle().Which.Should().Be("Bonus"); rules.Folders.Contains("BONUS").Should().BeTrue();
    }

    [Fact]
    public void BlankRows_FallBackToDefaults()
    {
        var rules = SidecarRules.From(new Dictionary<string, string> { [SidecarRules.ExtensionsKey] = "  , " });

        rules.Extensions.Should().Contain(".srt");
    }

    [Theory]
    [InlineData("Movie.en.srt", new string[0], "subtitle")]
    [InlineData("poster.jpg", new string[0], "artwork")]
    [InlineData("clip.mkv", new[] { "Extras" }, "extra")]
    [InlineData("theme.mp3", new[] { "theme-music" }, "theme")]
    [InlineData("fan1.jpg", new[] { "extrafanart" }, "artwork")]
    [InlineData("Album.cue", new string[0], "booklet")]
    [InlineData("readme.txt", new string[0], "other")]
    public void Classify_NamesTheKind(string file, string[] folders, string expected) =>
        SidecarRules.Defaults.Classify(file, folders).Should().Be(expected);
}

public class RelatedFileGroupingTests
{
    private readonly ScanGroupingService _svc = new(new FolderSignalExtractor(), new TagSignalExtractor());
    private static readonly ScanGroupOptions Collect = new(CollectRelatedFiles: true);

    [Fact]
    public void FlatMovieFolder_GetsItsSubtitlesAndArtwork_ButNoFilesAreImportedForThem()
    {
        var files = new[]
        {
            "C:/Movies/Alien (1979)/Alien (1979).mkv",
            "C:/Movies/Alien (1979)/Alien (1979).en.srt",
            "C:/Movies/Alien (1979)/poster.jpg",
            "C:/Movies/Aliens (1986)/Aliens (1986).mkv",
        };

        var result = _svc.Group(files, "C:/Movies", 1, Collect);

        var alien = result.Groups.Single(g => g.Name == "Alien (1979)");
        alien.Files.Should().ContainSingle();
        alien.RelatedFiles.Should().BeEquivalentTo(["C:/Movies/Alien (1979)/Alien (1979).en.srt", "C:/Movies/Alien (1979)/poster.jpg"]);
        result.Groups.Single(g => g.Name == "Aliens (1986)").RelatedFiles.Should().BeEmpty();
    }

    [Fact]
    public void WithoutTheOption_NothingIsCollected_AndGroupingIsUnchanged()
    {
        var files = new[] { "C:/Movies/Alien (1979)/Alien (1979).mkv", "C:/Movies/Alien (1979)/Alien (1979).srt" };

        var result = _svc.Group(files, "C:/Movies", 1);

        result.Groups.Single().RelatedFiles.Should().BeEmpty();
        result.Groups.Single().Files.Should().ContainSingle();
    }

    [Fact]
    public void TvShow_FilesGoToTheDeepestFolderThatOwnsThem()
    {
        var files = new[]
        {
            "C:/TV/Show/Season 1/Show - S01E01.mkv",
            "C:/TV/Show/Season 1/Show - S01E01.srt",
            "C:/TV/Show/Season 1/Season01-poster.jpg",
            "C:/TV/Show/poster.jpg",
            "C:/TV/Show/extras/Making Of.mkv",
        };

        var result = _svc.Group(files, "C:/TV", 3, Collect);

        var show = result.Groups.Single();
        show.RelatedFiles.Should().BeEquivalentTo(["C:/TV/Show/poster.jpg", "C:/TV/Show/extras/Making Of.mkv"]);
        var season = show.Children.Single();
        season.RelatedFiles.Should().BeEquivalentTo(["C:/TV/Show/Season 1/Show - S01E01.srt", "C:/TV/Show/Season 1/Season01-poster.jpg"]);
    }

    [Fact]
    public void LooseMovieFiles_AreMatchedByFileName()
    {
        var files = new[] { "C:/Movies/Heat.mkv", "C:/Movies/Heat.en.srt", "C:/Movies/Other.srt" };

        var result = _svc.Group(files, "C:/Movies", 1, Collect);

        result.Groups.Single().RelatedFiles.Should().BeEquivalentTo(["C:/Movies/Heat.en.srt"]);
    }

    [Fact]
    public void ConfiguredSidecarExtensions_AreHonoured()
    {
        var settings = new FakeSettings(new Dictionary<string, string> { [SidecarRules.ExtensionsKey] = ".vtt" });
        var svc = new ScanGroupingService(new FolderSignalExtractor(), new TagSignalExtractor(), settings);
        var files = new[] { "C:/Movies/Heat (1995)/Heat (1995).mkv", "C:/Movies/Heat (1995)/Heat (1995).vtt" };

        var result = svc.Group(files, "C:/Movies", 1, Collect);

        result.Groups.Single().RelatedFiles.Should().ContainSingle().Which.Should().EndWith(".vtt");
    }

    private sealed class FakeSettings(IReadOnlyDictionary<string, string> rows) : Chronicle.Services.Security.ICachedAppSettings
    {
        public IReadOnlyDictionary<string, string> Snapshot => rows;
    }
}

public class ScanHintsTests
{
    [Fact]
    public void Parse_BuildsPatternsAndExtensions()
    {
        var hints = ScanHints.Parse("{\"filePatterns\":[\"(?i)\\\\bS\\\\d+E\\\\d+\\\\b\"],\"extensions\":[\"mkv\",\".MP4\"]}");

        hints.Should().NotBeNull();
        hints!.MatchesDistinctively("C:/x/Show.S01E02.mkv").Should().BeTrue();
        hints.MatchesDistinctively("C:/x/Film.mkv").Should().BeFalse();
        hints.MatchesExtension("a.mkv").Should().BeTrue();
        hints.MatchesExtension("a.mp4").Should().BeTrue();
        hints.MatchesExtension("a.mp3").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void Parse_GivesNothingForEmptyOrBrokenInput(string? json) => ScanHints.Parse(json).Should().BeNull();

    [Fact]
    public void Parse_SkipsABadPattern_ButKeepsTheRest()
    {
        var hints = ScanHints.Parse("{\"filePatterns\":[\"(unclosed\",\"abc\"]}");

        hints!.FilePatterns.Should().HaveCount(1);
    }

    [Fact]
    public void FolderPatterns_MatchAnyFolderOnThePath()
    {
        var hints = ScanHints.Parse("{\"folderPatterns\":[\"(?i)^season\\\\s*\\\\d+$\"]}");

        hints!.MatchesDistinctively("C:/TV/Show/Season 2/ep.mkv").Should().BeTrue();
        hints.MatchesDistinctively("C:/TV/Show/Extras/ep.mkv").Should().BeFalse();
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("{\"extensions\":[\".mkv\"]}", null)]
    public void Validate_AcceptsGoodHints(string json, string? expected) => ScanHints.Validate(json).Should().Be(expected);

    [Theory]
    [InlineData("nope")]
    [InlineData("{\"filePatterns\":[\"(bad\"]}")]
    [InlineData("{\"extensions\":[\"\"]}")]
    public void Validate_ExplainsWhatIsWrong(string json) => ScanHints.Validate(json).Should().NotBeNullOrWhiteSpace();

    [Fact]
    public void Validate_RejectsBackReferences_ThatCouldBlowUpTheMatcher() =>
        ScanHints.Validate("{\"filePatterns\":[\"(a)\\\\1\"]}").Should().NotBeNull();
}

public class MediaTypeMismatchDetectorTests
{
    private const string TvHints = "{\"filePatterns\":[\"(?i)\\\\bS\\\\d{1,2}E\\\\d{1,3}\\\\b\"],\"folderPatterns\":[\"(?i)^season\\\\s*\\\\d+$\"],\"extensions\":[]}";
    private const string MovieHints = "{\"extensions\":[\".mkv\",\".mp4\"]}";
    private const string MusicHints = "{\"extensions\":[\".mp3\",\".flac\"]}";

    private static readonly MediaType Tv = new() { Id = 1, Name = "tv", DisplayName = "TV Shows", ScanHintsJson = TvHints };
    private static readonly MediaType Movies = new() { Id = 2, Name = "movies", DisplayName = "Movies", ScanHintsJson = MovieHints };
    private static readonly MediaType Music = new() { Id = 3, Name = "music", DisplayName = "Music", ScanHintsJson = MusicHints };
    private static readonly MediaType[] All = [Tv, Movies, Music];

    [Fact]
    public void EpisodeNamedFiles_InAMoviesFolder_AreFlaggedAsTv()
    {
        var files = new[] { "C:/M/Show/Show.S01E01.mkv", "C:/M/Show/Show.S01E02.mkv", "C:/M/Show/Show.S01E03.mkv" };

        var s = MediaTypeMismatchDetector.Detect(files, Movies, All);

        s.Should().NotBeNull();
        s!.MediaTypeId.Should().Be(1);
        s.Reason.Should().Contain("100%").And.Contain("TV Shows");
    }

    [Fact]
    public void Songs_InAMoviesFolder_AreFlaggedAsMusic_ByExtension()
    {
        var files = new[] { "C:/M/Album/01.mp3", "C:/M/Album/02.flac", "C:/M/Album/03.mp3" };

        MediaTypeMismatchDetector.Detect(files, Movies, All)!.MediaTypeName.Should().Be("Music");
    }

    [Fact]
    public void AMovieInAMoviesFolder_IsNotFlagged() =>
        MediaTypeMismatchDetector.Detect(["C:/M/Heat/Heat.mkv"], Movies, All).Should().BeNull();

    [Fact]
    public void TvShowWithPlainFileNames_IsNeverCalledMovies()
    {
        // TV lists no extensions, so the broad extension rule cannot fire against it.
        var files = new[] { "C:/T/Show/Season 1/one.mkv", "C:/T/Show/Season 1/two.mkv", "C:/T/Show/Season 1/three.mkv" };

        MediaTypeMismatchDetector.Detect(files, Tv, All).Should().BeNull();
    }

    [Fact]
    public void AMixedFolder_IsLeftAlone()
    {
        var files = new[] { "C:/M/a.S01E01.mkv", "C:/M/b.mkv", "C:/M/c.mkv", "C:/M/d.mkv" };

        MediaTypeMismatchDetector.Detect(files, Movies, All).Should().BeNull();
    }

    [Fact]
    public void ATypeWithoutHints_IsNeverFlagged()
    {
        var bare = new MediaType { Id = 9, Name = "comics", DisplayName = "Comics" };

        MediaTypeMismatchDetector.Detect(["C:/c/Show.S01E01.mkv"], bare, All).Should().BeNull();
    }

    [Fact]
    public void InactiveTypes_AreNeverSuggested()
    {
        var off = new MediaType { Id = 1, Name = "tv", DisplayName = "TV Shows", ScanHintsJson = TvHints, IsActive = false };

        MediaTypeMismatchDetector.Detect(["C:/M/Show.S01E01.mkv", "C:/M/Show.S01E02.mkv"], Movies, [off, Movies]).Should().BeNull();
    }

    [Fact]
    public void Annotate_MarksOnlyTheMismatchedGroups()
    {
        var bad = new ScanGroup { Name = "Show", Files = ["C:/M/Show.S01E01.mkv", "C:/M/Show.S01E02.mkv"] };
        var good = new ScanGroup { Name = "Heat", Files = ["C:/M/Heat.mkv"] };

        MediaTypeMismatchDetector.Annotate([bad, good], Movies, All);

        bad.SuggestedMediaTypeId.Should().Be(1);
        bad.SuggestedMediaTypeName.Should().Be("TV Shows");
        good.SuggestedMediaTypeId.Should().BeNull();
    }

    [Fact]
    public void Grouping_AnnotatesWhenAskedTo()
    {
        var svc = new ScanGroupingService(new FolderSignalExtractor(), new TagSignalExtractor());
        var files = new[] { "C:/Movies/Show/Show.S01E01.mkv", "C:/Movies/Show/Show.S01E02.mkv" };

        var result = svc.Group(files, "C:/Movies", 1, new ScanGroupOptions(MismatchCandidates: All, ScannedType: Movies));

        result.Groups.Single().SuggestedMediaTypeName.Should().Be("TV Shows");
    }

    [Fact]
    public void ScheduledScan_HoldsBackMismatches_UnlessToldToIgnoreThem()
    {
        var bad = new ScanGroup { Name = "Show", SuggestedMediaTypeId = 1 };
        var good = new ScanGroup { Name = "Heat" };

        var (import, held) = ScheduledScanService.HoldBackMismatches([bad, good], null);
        import.Should().BeEquivalentTo([good]);
        held.Should().BeEquivalentTo([bad]);

        var (importAll, heldNone) = ScheduledScanService.HoldBackMismatches([bad, good], "ignore");
        importAll.Should().HaveCount(2);
        heldNone.Should().BeEmpty();
    }
}

public class RelatedFilePersistenceTests
{
    private static ChronicleDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static FileScanService Service(ChronicleDbContext db) => new(db, null!, null!, null!, null!, null!);

    private static async Task<MediaItem> ItemAsync(ChronicleDbContext db)
    {
        var item = new MediaItem { Name = "Heat", MediaTypeId = 2 };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    [Fact]
    public async Task NewFiles_AreRecorded_WithTheirKind()
    {
        using var db = NewDb();
        var item = await ItemAsync(db);

        await Service(db).SyncRelatedFilesAsync(item, ["C:/m/Heat/Heat.en.srt", "C:/m/Heat/poster.jpg"], "C:/m/Heat", default);
        await db.SaveChangesAsync();

        var rows = await db.MediaItemRelatedFiles.ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(r => r.Path.EndsWith(".srt")).Kind.Should().Be(RelatedFileKinds.Subtitle);
        rows.Single(r => r.Path.EndsWith(".jpg")).Kind.Should().Be(RelatedFileKinds.Artwork);
    }

    [Fact]
    public async Task Rescanning_DoesNotDuplicate()
    {
        using var db = NewDb();
        var item = await ItemAsync(db);
        var svc = Service(db);

        await svc.SyncRelatedFilesAsync(item, ["C:/m/Heat/a.srt"], "C:/m/Heat", default);
        await db.SaveChangesAsync();
        await svc.SyncRelatedFilesAsync(item, ["C:/m/Heat/a.srt"], "C:/m/Heat", default);
        await db.SaveChangesAsync();

        (await db.MediaItemRelatedFiles.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AFileThatDisappears_IsMarkedMissing_NotDeleted_AndRecoversWhenItReturns()
    {
        using var db = NewDb();
        var item = await ItemAsync(db);
        var svc = Service(db);
        await svc.SyncRelatedFilesAsync(item, ["C:/m/Heat/a.srt"], "C:/m/Heat", default);
        await db.SaveChangesAsync();

        await svc.SyncRelatedFilesAsync(item, [], "C:/m/Heat", default);
        await db.SaveChangesAsync();
        (await db.MediaItemRelatedFiles.SingleAsync()).MissingSince.Should().NotBeNull();

        await svc.SyncRelatedFilesAsync(item, ["C:/m/Heat/a.srt"], "C:/m/Heat", default);
        await db.SaveChangesAsync();
        (await db.MediaItemRelatedFiles.SingleAsync()).MissingSince.Should().BeNull();
    }

    [Fact]
    public async Task AnotherFoldersScan_DoesNotMarkThisFoldersFilesMissing()
    {
        using var db = NewDb();
        var item = await ItemAsync(db);
        var svc = Service(db);
        await svc.SyncRelatedFilesAsync(item, ["C:/m/A/a.srt"], "C:/m/A", default);
        await db.SaveChangesAsync();

        await svc.SyncRelatedFilesAsync(item, ["C:/m/B/b.srt"], "C:/m/B", default);
        await db.SaveChangesAsync();

        (await db.MediaItemRelatedFiles.SingleAsync(r => r.Path.EndsWith("a.srt"))).MissingSince.Should().BeNull();
        (await db.MediaItemRelatedFiles.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task WithoutAFolder_NothingIsEverMarkedMissing()
    {
        using var db = NewDb();
        var item = await ItemAsync(db);
        var svc = Service(db);
        await svc.SyncRelatedFilesAsync(item, ["C:/m/A/a.srt"], null, default);
        await db.SaveChangesAsync();

        await svc.SyncRelatedFilesAsync(item, [], null, default);
        await db.SaveChangesAsync();

        (await db.MediaItemRelatedFiles.SingleAsync()).MissingSince.Should().BeNull();
    }

    [Fact]
    public async Task PathMatching_IgnoresCase()
    {
        using var db = NewDb();
        var item = await ItemAsync(db);
        var svc = Service(db);
        await svc.SyncRelatedFilesAsync(item, ["C:/m/Heat/A.srt"], "C:/m/Heat", default);
        await db.SaveChangesAsync();

        await svc.SyncRelatedFilesAsync(item, ["c:/m/heat/a.SRT"], "C:/m/Heat", default);
        await db.SaveChangesAsync();

        (await db.MediaItemRelatedFiles.CountAsync()).Should().Be(1);
    }
}

public class MediaFileRulesTests
{
    private sealed class FakeSettings(IReadOnlyDictionary<string, string> rows) : Chronicle.Services.Security.ICachedAppSettings
    {
        public IReadOnlyDictionary<string, string> Snapshot => rows;
    }

    [Fact]
    public void WithNothingConfigured_OnlyTheBuiltInTypesCount()
    {
        var rules = MediaFileRules.From(null);

        rules.Recognized.Should().Contain(".mkv").And.Contain(".flac").And.NotContain(".rmvb");
        rules.Audio.Should().Contain(".flac").And.NotContain(".mkv");
    }

    [Fact]
    public void ExtraTypes_AreAdded_WithOrWithoutADot_AndAudioOnesAreAudio()
    {
        var rules = MediaFileRules.From(new Dictionary<string, string>
        {
            [MediaFileRules.ExtraVideoKey] = "rmvb, .Webm2",
            [MediaFileRules.ExtraAudioKey] = "dsf",
        });

        rules.Recognized.Should().Contain(".rmvb").And.Contain(".webm2").And.Contain(".dsf");
        rules.Audio.Should().Contain(".dsf").And.NotContain(".rmvb");
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData(".")]
    [InlineData("toolongextension1")]
    [InlineData("with space")]
    public void ThingsThatAreNotPlainExtensions_AreIgnored(string junk)
    {
        var rules = MediaFileRules.From(new Dictionary<string, string> { [MediaFileRules.ExtraVideoKey] = junk });

        rules.Recognized.Count.Should().Be(MediaFileRules.Defaults.Recognized.Count);
    }

    [Fact]
    public void Grouping_ImportsAFileTypeTheAdministratorAdded_AndStillSkipsUnknownOnes()
    {
        var settings = new Dictionary<string, string> { [MediaFileRules.ExtraVideoKey] = ".rmvb" };
        var svc = new ScanGroupingService(new FolderSignalExtractor(), new TagSignalExtractor(), new FakeSettings(settings));
        var files = new[] { "C:/Movies/Old Film (1999)/Old Film (1999).rmvb", "C:/Movies/Other (2000)/Other (2000).xyz" };

        var result = svc.Group(files, "C:/Movies", 1);

        result.Groups.Select(g => g.Name).Should().Equal("Old Film (1999)");
    }

    [Fact]
    public void Grouping_WithoutTheSetting_SkipsThatFileType()
    {
        var svc = new ScanGroupingService(new FolderSignalExtractor(), new TagSignalExtractor());

        var result = svc.Group(["C:/Movies/Old Film (1999)/Old Film (1999).rmvb"], "C:/Movies", 1);

        result.Groups.Should().BeEmpty();
    }

    [Fact]
    public void AnExtraAudioType_GetsItsTrackNumbersReadFromTheName()
    {
        var settings = new Dictionary<string, string> { [MediaFileRules.ExtraAudioKey] = "dsf" };
        var svc = new ScanGroupingService(new FolderSignalExtractor(), new TagSignalExtractor(), new FakeSettings(settings));

        var result = svc.Group(["C:/Music/Artist/Album/01 - First.dsf", "C:/Music/Artist/Album/02 - Second.dsf"], "C:/Music", 3);

        result.Groups.Single().Children.Single().Children.Select(t => t.Name).Should().Equal("First", "Second");
    }
}
