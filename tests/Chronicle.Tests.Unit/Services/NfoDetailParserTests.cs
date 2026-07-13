using Chronicle.Services.Scan;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class NfoDetailParserTests
{
    private const string SampleXml = """
        <movie>
          <title>2 Fast 2 Furious</title>
          <originaltitle>2 Fast 2 Furious</originaltitle>
          <ratings>
            <rating name="imdb" max="10" default="true">
              <value>6.0</value>
              <votes>325462</votes>
            </rating>
          </ratings>
          <rating>6.524</rating>
          <plot>It's a major double-cross...</plot>
          <runtime>108</runtime>
          <thumb aspect="poster">https://image.tmdb.org/thumb.jpg</thumb>
          <mpaa>14A</mpaa>
          <uniqueid type="tmdb" default="true">584</uniqueid>
          <uniqueid type="imdb">tt0322259</uniqueid>
          <genre>Action</genre>
          <genre>Crime</genre>
          <genre>Thriller</genre>
          <set tmdbcolid="9485">
            <name>The Fast and the Furious Collection</name>
          </set>
          <credits>Michael Brandt</credits>
          <credits>Derek Haas</credits>
          <director>John Singleton</director>
          <premiered>2003-06-05</premiered>
          <year>2003</year>
          <studio>Ardustry Entertainment</studio>
          <actor>
            <name>Paul Walker</name>
            <role>Brian O'Conner</role>
            <order>0</order>
          </actor>
          <actor>
            <name>Tyrese Gibson</name>
            <role>Roman Pearce</role>
            <order>1</order>
          </actor>
        </movie>
        """;

    [Fact]
    public void ParseXml_FullNfo_ExtractsAllRichFields()
    {
        var parser = new NfoDetailParser();

        var detail = parser.ParseXml(SampleXml);

        detail.Should().NotBeNull();
        detail!.Title.Should().Be("2 Fast 2 Furious");
        detail.OriginalTitle.Should().Be("2 Fast 2 Furious");
        detail.Plot.Should().Be("It's a major double-cross...");
        detail.Genres.Should().Equal("Action", "Crime", "Thriller");
        detail.Mpaa.Should().Be("14A");
        detail.Studio.Should().Be("Ardustry Entertainment");
        detail.RuntimeMinutes.Should().Be(108);
        detail.Premiered.Should().Be("2003-06-05");
        detail.Director.Should().Be("John Singleton");
        detail.Writers.Should().Equal("Michael Brandt", "Derek Haas");
        detail.CollectionName.Should().Be("The Fast and the Furious Collection");
    }

    [Fact]
    public void ParseXml_RatingsBlock_PrefersDefaultRatingOverFlatRating()
    {
        var parser = new NfoDetailParser();

        var detail = parser.ParseXml(SampleXml);

        // The <ratings><rating default="true"> value (6.0) should win over the legacy
        // flat <rating>6.524</rating> element.
        detail!.Rating.Should().Be(6.0);
    }

    [Fact]
    public void ParseXml_NoRatingsBlock_FallsBackToFlatRatingElement()
    {
        var parser = new NfoDetailParser();
        const string xml = "<movie><title>Solo Film</title><rating>7.2</rating></movie>";

        var detail = parser.ParseXml(xml);

        detail!.Rating.Should().Be(7.2);
    }

    [Fact]
    public void ParseXml_Actors_OrderedByOrderElementAndCapped()
    {
        var parser = new NfoDetailParser();

        var detail = parser.ParseXml(SampleXml);

        detail!.Actors.Should().HaveCount(2);
        detail.Actors[0].Name.Should().Be("Paul Walker");
        detail.Actors[0].Role.Should().Be("Brian O'Conner");
        detail.Actors[1].Name.Should().Be("Tyrese Gibson");
    }

    [Fact]
    public void ParseXml_ActorMissingName_IsSkipped()
    {
        var parser = new NfoDetailParser();
        const string xml = """
            <movie>
              <title>Test</title>
              <actor><role>No Name Here</role></actor>
              <actor><name>Real Actor</name><role>Lead</role></actor>
            </movie>
            """;

        var detail = parser.ParseXml(xml);

        detail!.Actors.Should().ContainSingle();
        detail.Actors[0].Name.Should().Be("Real Actor");
    }

    [Fact]
    public void ParseXml_MalformedXml_ReturnsNull()
    {
        var parser = new NfoDetailParser();

        var detail = parser.ParseXml("<movie><title>Unclosed");

        detail.Should().BeNull();
    }

    [Fact]
    public void ParseXml_EmptyString_ReturnsNull()
    {
        var parser = new NfoDetailParser();

        parser.ParseXml("").Should().BeNull();
        parser.ParseXml("   ").Should().BeNull();
    }

    [Fact]
    public void Parse_MissingFile_ReturnsNull()
    {
        var parser = new NfoDetailParser();

        var detail = parser.Parse(@"C:\does\not\exist\movie.nfo");

        detail.Should().BeNull();
    }

    [Fact]
    public void ParseXml_MinimalMovie_NoOptionalFieldsPresent()
    {
        var parser = new NfoDetailParser();
        const string xml = "<movie><title>Bare Bones</title></movie>";

        var detail = parser.ParseXml(xml);

        detail.Should().NotBeNull();
        detail!.Title.Should().Be("Bare Bones");
        detail.Plot.Should().BeNull();
        detail.Rating.Should().BeNull();
        detail.Genres.Should().BeEmpty();
        detail.Writers.Should().BeEmpty();
        detail.Actors.Should().BeEmpty();
        detail.CollectionName.Should().BeNull();
    }
}
