using System.Text.Json.Nodes;
using Chronicle.Services.Scan;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class FileIdentityJsonTests
{
    [Fact]
    public void ComputeFingerprint_SameSizeAndModifiedTime_ProducesSameFingerprint()
    {
        var modified = new DateTime(2026, 7, 9, 21, 14, 0, DateTimeKind.Utc);
        FileIdentityJson.ComputeFingerprint(48213112, modified)
            .Should().Be(FileIdentityJson.ComputeFingerprint(48213112, modified));
    }

    [Fact]
    public void ComputeFingerprint_DifferentSize_ProducesDifferentFingerprint()
    {
        var modified = new DateTime(2026, 7, 9, 21, 14, 0, DateTimeKind.Utc);
        FileIdentityJson.ComputeFingerprint(48213112, modified)
            .Should().NotBe(FileIdentityJson.ComputeFingerprint(48213999, modified));
    }

    [Fact]
    public void ApplyIfChanged_FirstApplication_ReturnsChangedTrue()
    {
        var node = new JsonObject();
        var snapshot = new FileIdentitySnapshot(48213112, DateTime.UtcNow, 320, 44100, 245, "mp3");

        var changed = FileIdentityJson.ApplyIfChanged(node, snapshot);

        changed.Should().BeTrue();
        node["fileSizeBytes"]!.GetValue<long?>().Should().Be(48213112);
        node["bitrateKbps"]!.GetValue<int?>().Should().Be(320);
        node["fileType"]!.GetValue<string?>().Should().Be("mp3");
    }

    [Fact]
    public void ApplyIfChanged_SameFingerprintReapplied_ReturnsChangedFalse()
    {
        var node = new JsonObject();
        var modified = new DateTime(2026, 7, 9, 21, 14, 0, DateTimeKind.Utc);
        var snapshot = new FileIdentitySnapshot(48213112, modified, 320, 44100, 245, "mp3");

        FileIdentityJson.ApplyIfChanged(node, snapshot);
        var secondCallChanged = FileIdentityJson.ApplyIfChanged(node, snapshot);

        secondCallChanged.Should().BeFalse();
    }

    [Fact]
    public void ApplyIfChanged_FileSizeChanged_ReturnsChangedTrueAndUpdatesFields()
    {
        var node = new JsonObject();
        var modified = new DateTime(2026, 7, 9, 21, 14, 0, DateTimeKind.Utc);
        FileIdentityJson.ApplyIfChanged(node, new FileIdentitySnapshot(48213112, modified, 320, 44100, 245, "mp3"));

        // Re-encoded at a different bitrate — file size changed even though modified time didn't move.
        var changed = FileIdentityJson.ApplyIfChanged(node, new FileIdentitySnapshot(96000000, modified, 1411, 44100, 245, "flac"));

        changed.Should().BeTrue();
        node["fileSizeBytes"]!.GetValue<long?>().Should().Be(96000000);
        node["bitrateKbps"]!.GetValue<int?>().Should().Be(1411);
        node["fileType"]!.GetValue<string?>().Should().Be("flac");
    }

    // ── ExtractFilePaths / PrimaryFilePathKey / ContainsFilePath ──────────────

    private static string Meta(params string[] filePaths) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            fileScanner = new { filePaths, folderPath = "/media/Movies/X" }
        });

    [Fact]
    public void ExtractFilePaths_ReturnsAllEntries()
    {
        FileIdentityJson.ExtractFilePaths(Meta("/a/one.mkv", "/a/two.srt"))
            .Should().BeEquivalentTo(["/a/one.mkv", "/a/two.srt"]);
    }

    [Fact]
    public void ExtractFilePaths_NoFileScannerSection_ReturnsEmpty()
    {
        var meta = System.Text.Json.JsonSerializer.Serialize(new { tmdb = new { id = 603 } });
        FileIdentityJson.ExtractFilePaths(meta).Should().BeEmpty();
    }

    [Fact]
    public void ExtractFilePaths_MalformedJson_ReturnsEmpty()
    {
        FileIdentityJson.ExtractFilePaths("{not json").Should().BeEmpty();
    }

    [Fact]
    public void PrimaryFilePathKey_IsOrderIndependent()
    {
        // Same two files listed in a different order must still produce the same key,
        // so re-scans that enumerate files in a different order still group as duplicates.
        FileIdentityJson.PrimaryFilePathKey(Meta("/a/two.srt", "/a/one.mkv"))
            .Should().Be(FileIdentityJson.PrimaryFilePathKey(Meta("/a/one.mkv", "/a/two.srt")));
    }

    [Fact]
    public void PrimaryFilePathKey_NoFilePaths_ReturnsNull()
    {
        FileIdentityJson.PrimaryFilePathKey(null).Should().BeNull();
    }

    [Fact]
    public void ContainsFilePath_ExactCaseInsensitiveMatch_ReturnsTrue()
    {
        FileIdentityJson.ContainsFilePath(Meta("/a/One.mkv"), "/a/one.MKV").Should().BeTrue();
    }

    [Fact]
    public void ContainsFilePath_NoMatch_ReturnsFalse()
    {
        FileIdentityJson.ContainsFilePath(Meta("/a/one.mkv"), "/a/two.mkv").Should().BeFalse();
    }

    [Fact]
    public void ContainsAnyFilePath_OneOfManyMatches_ReturnsTrue()
    {
        FileIdentityJson.ContainsAnyFilePath(Meta("/a/one.mkv"), ["/x/other.mkv", "/a/one.mkv"])
            .Should().BeTrue();
    }

    [Fact]
    public void ContainsAnyFilePath_NoneMatch_ReturnsFalse()
    {
        FileIdentityJson.ContainsAnyFilePath(Meta("/a/one.mkv"), ["/x/other.mkv"])
            .Should().BeFalse();
    }
}
