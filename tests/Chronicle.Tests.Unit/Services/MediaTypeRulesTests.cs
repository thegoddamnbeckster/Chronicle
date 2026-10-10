using Chronicle.Core.Models;
using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class MediaTypeRulesTests
{
    private static MediaTypeInput Good(Action<Dictionary<string, object?>>? tweak = null)
    {
        var v = new Dictionary<string, object?>
        {
            ["Name"] = "comics", ["DisplayName"] = "Comics", ["Description"] = "Comic books", ["Levels"] = 3,
            ["Labels"] = new[] { "Series", "Volume", "Issue" }, ["Verb"] = "read", ["Unit"] = "pages",
            ["Collections"] = false, ["Trackable"] = true, ["Scan"] = (string?)null, ["Active"] = true,
        };
        tweak?.Invoke(v);
        return new MediaTypeInput((string?)v["Name"], (string)v["DisplayName"]!, (string?)v["Description"], (int)v["Levels"]!,
            (string[])v["Labels"]!, (string)v["Verb"]!, (string)v["Unit"]!, (bool)v["Collections"]!, (bool)v["Trackable"]!,
            (string?)v["Scan"], (bool)v["Active"]!);
    }

    [Fact]
    public void AGoodType_IsAccepted_ForCreateAndUpdate()
    {
        MediaTypeRules.Validate(Good(), creating: true).Should().BeNull();
        MediaTypeRules.Validate(Good(v => v["Name"] = null), creating: false).Should().BeNull("the name is not part of an edit");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("Comics")]
    [InlineData("my comics")]
    [InlineData("-comics")]
    [InlineData("comics!")]
    [InlineData("../comics")]
    [InlineData("a-very-long-name-that-goes-on-and-on-and-on-and-on-and-on-and-on")]
    public void BadInternalNames_AreRefused_OnlyWhenCreating(string name)
    {
        MediaTypeRules.Validate(Good(v => v["Name"] = name), creating: true).Should().Contain("internal name");
        MediaTypeRules.Validate(Good(v => v["Name"] = name), creating: false).Should().BeNull();
    }

    [Theory]
    [InlineData("comics")]
    [InlineData("graphic-novels")]
    [InlineData("board_games")]
    [InlineData("4k")]
    public void GoodInternalNames_AreAccepted(string name)
    {
        MediaTypeRules.Validate(Good(v => v["Name"] = name), creating: true).Should().BeNull();
    }

    [Fact]
    public void DisplayName_AndDescription_AreLimited()
    {
        MediaTypeRules.Validate(Good(v => v["DisplayName"] = "   "), true).Should().Contain("display name");
        MediaTypeRules.Validate(Good(v => v["DisplayName"] = new string('x', 61)), true).Should().Contain("display name");
        MediaTypeRules.Validate(Good(v => v["Description"] = new string('x', 301)), true).Should().Contain("description");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void LevelCount_MustBeBetweenOneAndFive(int levels)
    {
        MediaTypeRules.Validate(Good(v => { v["Levels"] = levels; v["Labels"] = Enumerable.Repeat("x", Math.Max(levels, 0)).ToArray(); }), true)
            .Should().Contain("levels");
    }

    [Fact]
    public void ThereMustBeOneLabelPerLevel_EachShortAndWithoutCommas()
    {
        MediaTypeRules.Validate(Good(v => v["Labels"] = new[] { "A", "B" }), true).Should().Contain("one label for each");
        MediaTypeRules.Validate(Good(v => v["Labels"] = new[] { "A", "", "C" }), true).Should().Contain("level label");
        MediaTypeRules.Validate(Good(v => v["Labels"] = new[] { "A", "B,C", "D" }), true).Should().Contain("comma");
        MediaTypeRules.Validate(Good(v => v["Labels"] = new[] { "A", new string('x', 31), "C" }), true).Should().Contain("level label");
    }

    [Theory]
    [InlineData("Watched")]
    [InlineData("w")]
    [InlineData("watch ed")]
    [InlineData("watched1")]
    [InlineData("")]
    public void TheActionWord_MustBeAPlainLowercaseWord(string verb)
    {
        MediaTypeRules.Validate(Good(v => v["Verb"] = verb), true).Should().Contain("action word");
    }

    [Theory]
    [InlineData("tasted")]
    [InlineData("watched")]
    [InlineData("listened")]
    public void AnyPlainPastTenseWordIsAllowed_NotJustTheOnesTheInterfaceKnows(string verb)
    {
        MediaTypeRules.Validate(Good(v => v["Verb"] = verb), true).Should().BeNull();
    }

    [Fact]
    public void ProgressUnit_AndScanStrategy_AreChecked()
    {
        MediaTypeRules.Validate(Good(v => v["Unit"] = "Pages!"), true).Should().Contain("progress unit");
        MediaTypeRules.Validate(Good(v => v["Scan"] = "bogus"), true).Should().Contain("scan style");
        MediaTypeRules.Validate(Good(v => v["Scan"] = "audiobook"), true).Should().BeNull();
    }

    [Fact]
    public void Labels_RoundTripThroughTheStoredCommaForm()
    {
        MediaTypeRules.JoinLabels([" Show ", "Season", "Episode"]).Should().Be("Show,Season,Episode");
        MediaTypeRules.SplitLabels("Show, Season ,Episode").Should().Equal("Show", "Season", "Episode");
        MediaTypeRules.SplitLabels(null).Should().BeEmpty();
        MediaTypeRules.SplitLabels("  ").Should().BeEmpty();
    }

    [Theory]
    [InlineData("audiobooks", "audiobook")]
    [InlineData("Audiobooks", "audiobook")]
    [InlineData("movies", null)]
    [InlineData("comics", null)]
    public void ScanStrategyDefault_IsAppliedOnlyForAKnownName_AndOnlyAsAStartingValue(string name, string? expected)
    {
        ScanStrategies.DefaultFor(name).Should().Be(expected);
    }
}
