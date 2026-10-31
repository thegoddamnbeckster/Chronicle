using System.Text.Json;
using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class MetadataResolutionImageFormatTests
{
    private static JsonElement Str(string s) => JsonSerializer.SerializeToElement(s);

    [Theory]
    [InlineData("https://upload.wikimedia.org/wikipedia/commons/d/df/The_Voice_logo.svg?utm_source=en.wikipedia.org&utm_campaign=api")]
    [InlineData("https://example.com/a/b.SVG")]
    [InlineData("https://example.com/a/b.svg#frag")]
    public void A_vector_url_is_recognised_even_with_a_query_string(string url) =>
        MetadataResolutionService.IsVectorImageUrl(url).Should().BeTrue();

    [Theory]
    [InlineData("https://static.tvmaze.com/uploads/images/original_untouched/637/1594810.jpg")]
    [InlineData("https://example.com/svg/poster.png")]
    [InlineData("https://example.com/a.svg.png")]
    [InlineData("")]
    [InlineData(null)]
    public void A_raster_or_empty_url_is_not_a_vector(string? url) =>
        MetadataResolutionService.IsVectorImageUrl(url).Should().BeFalse();

    [Theory]
    [InlineData("posterUrl")]
    [InlineData("backdropUrl")]
    [InlineData("logoUrl")]
    [InlineData("bannerUrl")]
    [InlineData("thumbUrl")]
    [InlineData("clearartUrl")]
    [InlineData("discUrl")]
    [InlineData("characterArtUrl")]
    public void An_svg_is_not_usable_in_any_artwork_slot_but_a_jpg_is(string key)
    {
        MetadataResolutionService.IsUsable(key, Str("https://x.org/logo.svg?a=b")).Should().BeFalse();
        MetadataResolutionService.IsUsable(key, Str("https://x.org/poster.jpg")).Should().BeTrue();
    }

    [Fact]
    public void Text_fields_are_unaffected() =>
        MetadataResolutionService.IsUsable("overview", Str("About the file logo.svg")).Should().BeTrue();
}
