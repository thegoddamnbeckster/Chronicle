using System.Text.Json;
using Chronicle.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Core.Helpers;

public class XmlToJsonConverterTests
{
    [Fact]
    public void ToJson_EmptyOrWhitespace_ReturnsNull()
    {
        XmlToJsonConverter.ToJson(null).Should().BeNull();
        XmlToJsonConverter.ToJson("").Should().BeNull();
        XmlToJsonConverter.ToJson("   ").Should().BeNull();
    }

    [Fact]
    public void ToJson_MalformedXml_ReturnsNull()
    {
        XmlToJsonConverter.ToJson("<movie><title>Unclosed").Should().BeNull();
    }

    [Fact]
    public void ToJson_SimpleLeafElement_BecomesPlainString()
    {
        var json = XmlToJsonConverter.ToJson("<movie><title>2 Fast 2 Furious</title></movie>");

        json!.Value.GetProperty("title").GetString().Should().Be("2 Fast 2 Furious");
    }

    [Fact]
    public void ToJson_RepeatedSiblingElements_BecomeArray()
    {
        var json = XmlToJsonConverter.ToJson(
            "<movie><genre>Action</genre><genre>Crime</genre><genre>Thriller</genre></movie>");

        var genres = json!.Value.GetProperty("genre");
        genres.ValueKind.Should().Be(JsonValueKind.Array);
        genres.EnumerateArray().Select(e => e.GetString()).Should()
            .Equal("Action", "Crime", "Thriller");
    }

    [Fact]
    public void ToJson_SingleOccurrenceElement_IsNotWrappedInArray()
    {
        // Only one <actor> present -- must NOT become a 1-item array, so a caller reading
        // this generically doesn't need to special-case "is this field sometimes an array".
        // Actually Kodi NFOs commonly have exactly one <director>; that's the case tested here.
        var json = XmlToJsonConverter.ToJson("<movie><director>John Singleton</director></movie>");

        json!.Value.GetProperty("director").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public void ToJson_AttributesArePreserved()
    {
        // Exactly the shape a real Kodi NFO's <uniqueid> and <rating> elements use --
        // attribute-bearing elements were entirely invisible to the old curated parser.
        var json = XmlToJsonConverter.ToJson(
            "<movie><uniqueid type=\"tmdb\" default=\"true\">584</uniqueid></movie>");

        var uid = json!.Value.GetProperty("uniqueid");
        uid.GetProperty("@type").GetString().Should().Be("tmdb");
        uid.GetProperty("@default").GetString().Should().Be("true");
        uid.GetProperty("#text").GetString().Should().Be("584");
    }

    [Fact]
    public void ToJson_NestedStreamDetails_AreFullyPreserved()
    {
        // The exact class of data the old parsers dropped entirely: audio/video technical
        // details (codec, channels -- e.g. Atmos) that only live inside <fileinfo>
        // <streamdetails>, never read by NfoDetailParser or NfoSignalExtractor before.
        const string xml = """
            <movie>
              <fileinfo>
                <streamdetails>
                  <audio>
                    <codec>truehd</codec>
                    <channels>8</channels>
                  </audio>
                  <video>
                    <codec>hevc</codec>
                    <width>3840</width>
                  </video>
                </streamdetails>
              </fileinfo>
            </movie>
            """;

        var json = XmlToJsonConverter.ToJson(xml);

        var audio = json!.Value.GetProperty("fileinfo").GetProperty("streamdetails").GetProperty("audio");
        audio.GetProperty("codec").GetString().Should().Be("truehd");
        audio.GetProperty("channels").GetString().Should().Be("8");

        var video = json.Value.GetProperty("fileinfo").GetProperty("streamdetails").GetProperty("video");
        video.GetProperty("codec").GetString().Should().Be("hevc");
    }

    [Fact]
    public void ToJson_ElementWithBothAttributeAndChildElement_KeepsBoth()
    {
        var json = XmlToJsonConverter.ToJson(
            "<movie><set tmdbcolid=\"9485\"><name>The Fast and the Furious Collection</name></set></movie>");

        var set = json!.Value.GetProperty("set");
        set.GetProperty("@tmdbcolid").GetString().Should().Be("9485");
        set.GetProperty("name").GetString().Should().Be("The Fast and the Furious Collection");
    }

    [Fact]
    public void ToJson_RoundTripsAFullRealisticNfo_NothingIsDropped()
    {
        const string xml = """
            <episodedetails>
              <title>Pilot</title>
              <showtitle>Breaking Bad</showtitle>
              <season>1</season>
              <episode>1</episode>
              <aired>2008-01-20</aired>
              <plot>A high school chemistry teacher...</plot>
              <credits>Vince Gilligan</credits>
              <director>Vince Gilligan</director>
              <rating>8.2</rating>
              <uniqueid type="tvdb">349232</uniqueid>
              <fileinfo>
                <streamdetails>
                  <audio><codec>ac3</codec><channels>6</channels></audio>
                </streamdetails>
              </fileinfo>
            </episodedetails>
            """;

        var json = XmlToJsonConverter.ToJson(xml);

        json!.Value.GetProperty("title").GetString().Should().Be("Pilot");
        json.Value.GetProperty("aired").GetString().Should().Be("2008-01-20");
        json.Value.GetProperty("uniqueid").GetProperty("@type").GetString().Should().Be("tvdb");
        json.Value.GetProperty("uniqueid").GetProperty("#text").GetString().Should().Be("349232");
        json.Value.GetProperty("fileinfo").GetProperty("streamdetails")
            .GetProperty("audio").GetProperty("codec").GetString().Should().Be("ac3");
    }
}
