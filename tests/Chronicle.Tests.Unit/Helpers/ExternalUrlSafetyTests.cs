using Chronicle.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Helpers;

/// <summary>
/// Covers the SSRF guard used before Chronicle's own server ever fetches a caller-supplied
/// URL (image-override pinning, the poster-proxy endpoint) -- a literal IP in the URL resolves
/// without a real DNS lookup, so these run with no network dependency.
/// </summary>
public class ExternalUrlSafetyTests
{
    [Theory]
    [InlineData("https://images.example.com/poster.jpg", true)]
    [InlineData("http://images.example.com/poster.jpg", true)]
    [InlineData("ftp://images.example.com/poster.jpg", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:image/png;base64,abc", false)]
    [InlineData("not a url at all", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWellFormedHttpUrl_OnlyAcceptsAbsoluteHttpOrHttps(string? url, bool expected)
    {
        ExternalUrlSafety.IsWellFormedHttpUrl(url, out _).Should().Be(expected);
    }

    [Theory]
    [InlineData("http://8.8.8.8/x.jpg", true)]         // public
    [InlineData("http://1.1.1.1/x.jpg", true)]          // public
    [InlineData("http://127.0.0.1/x.jpg", false)]       // loopback
    [InlineData("http://10.0.0.5/x.jpg", false)]        // RFC1918
    [InlineData("http://172.16.0.5/x.jpg", false)]      // RFC1918
    [InlineData("http://172.31.255.254/x.jpg", false)]  // RFC1918 (upper bound of /12)
    [InlineData("http://172.32.0.1/x.jpg", true)]        // just outside the /12 range -- public
    [InlineData("http://192.168.1.1/x.jpg", false)]     // RFC1918
    [InlineData("http://169.254.169.254/x.jpg", false)] // link-local / cloud metadata
    [InlineData("http://100.64.0.1/x.jpg", false)]      // carrier-grade NAT
    [InlineData("http://0.0.0.0/x.jpg", false)]
    public async Task IsSafeToFetchAsync_ClassifiesLiteralIPv4CorrectlyWithNoRealDns(string url, bool expectedSafe)
    {
        ExternalUrlSafety.IsWellFormedHttpUrl(url, out var uri).Should().BeTrue();
        (await ExternalUrlSafety.IsSafeToFetchAsync(uri!)).Should().Be(expectedSafe);
    }

    [Fact]
    public async Task IsSafeToFetchAsync_RejectsIPv6Loopback()
    {
        ExternalUrlSafety.IsWellFormedHttpUrl("http://[::1]/x.jpg", out var uri).Should().BeTrue();
        (await ExternalUrlSafety.IsSafeToFetchAsync(uri!)).Should().BeFalse();
    }

    [Fact]
    public async Task IsSafeToFetchAsync_RejectsIPv6UniqueLocal()
    {
        ExternalUrlSafety.IsWellFormedHttpUrl("http://[fd00::1]/x.jpg", out var uri).Should().BeTrue();
        (await ExternalUrlSafety.IsSafeToFetchAsync(uri!)).Should().BeFalse();
    }

    [Fact]
    public async Task IsSafeToFetchAsync_RejectsUnresolvableHost()
    {
        ExternalUrlSafety.IsWellFormedHttpUrl(
            "http://this-host-should-never-resolve.invalid/x.jpg", out var uri).Should().BeTrue();
        (await ExternalUrlSafety.IsSafeToFetchAsync(uri!)).Should().BeFalse();
    }
}
