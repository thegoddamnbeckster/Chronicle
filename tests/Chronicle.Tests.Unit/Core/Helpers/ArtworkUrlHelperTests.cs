using Chronicle.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Core.Helpers;

public class ArtworkUrlHelperTests
{
    [Fact]
    public void Encode_SpaceInFileName_BecomesPercent20_AndParenthesesStay()
    {
        // The real Supernatural thumb URL that Kodi's curl rejected (live, 2026-10-03).
        ArtworkUrlHelper.Encode("https://assets.fanart.tv/fanart/S_78901 (1).jpg")
            .Should().Be("https://assets.fanart.tv/fanart/S_78901%20(1).jpg");
    }

    [Theory]
    [InlineData("https://image.tmdb.org/t/p/w500/abc123.jpg")]
    [InlineData("https://assets.fanart.tv/fanart/supernatural-504af30ed43f7.png")]
    [InlineData("https://x.test/a%20b/c%28d%29.jpg")]            // already encoded: untouched
    [InlineData("https://x.test/p?q=1&r=a+b#frag")]
    public void Encode_AValidUrl_IsReturnedUnchanged(string url)
    {
        ArtworkUrlHelper.Encode(url).Should().Be(url);
    }

    [Fact]
    public void Encode_IsIdempotent()
    {
        var once = ArtworkUrlHelper.Encode("https://x.test/my image (2).jpg");
        ArtworkUrlHelper.Encode(once).Should().Be(once);
    }

    [Fact]
    public void Encode_TabsAndNewlinesAreEncodedToo()
    {
        ArtworkUrlHelper.Encode("https://x.test/a\tb\nc.jpg").Should().Be("https://x.test/a%09b%0Ac.jpg");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Encode_NullOrEmpty_PassesThrough(string? url)
    {
        ArtworkUrlHelper.Encode(url).Should().Be(url);
    }
}
