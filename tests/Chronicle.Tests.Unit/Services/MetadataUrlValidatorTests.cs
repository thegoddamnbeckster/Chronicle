using System.Net;
using System.Net.Sockets;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Chronicle.Tests.Unit.Services;

public class MetadataUrlValidatorTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add((request.Method, request.RequestUri!.AbsoluteUri));
            return Task.FromResult(respond(request));
        }
    }

    private static MetadataUrlValidator Make(Handler handler, IMemoryCache? cache = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("url-validation")).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new MetadataUrlValidator(factory.Object, NullLogger<MetadataUrlValidator>.Instance, cache);
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    [Fact]
    public async Task AGoodImage_IsKept_AndAMissingOneIsDropped()
    {
        var handler = new Handler(r => Status(r.RequestUri!.AbsolutePath.Contains("gone") ? HttpStatusCode.NotFound : HttpStatusCode.OK));
        var meta = new MediaMetadata { PosterUrl = "https://img.example.com/p.jpg", BackdropUrl = "https://img.example.com/gone.jpg" };

        await Make(handler).ValidateAndCleanAsync(meta);

        meta.PosterUrl.Should().Be("https://img.example.com/p.jpg");
        meta.BackdropUrl.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task ABusyServer_DoesNotCostTheImage(HttpStatusCode busy)
    {
        var handler = new Handler(_ => Status(busy));
        var meta = new MediaMetadata { PosterUrl = "https://img.example.com/p.jpg" };

        await Make(handler).ValidateAndCleanAsync(meta);

        meta.PosterUrl.Should().Be("https://img.example.com/p.jpg", "a rate limit says nothing about whether the image exists");
    }

    [Fact]
    public async Task AHostThatDoesNotExist_DropsTheImage_ButATimeoutDoesNot()
    {
        var dns = new Handler(_ => throw new HttpRequestException("no such host", new SocketException((int)SocketError.HostNotFound)));
        var timeout = new Handler(_ => throw new TaskCanceledException("timed out"));

        var bad = new MediaMetadata { PosterUrl = "https://nope.example.invalid/p.jpg" };
        await Make(dns).ValidateAndCleanAsync(bad);
        bad.PosterUrl.Should().BeNull();

        var slow = new MediaMetadata { PosterUrl = "https://slow.example.com/p.jpg" };
        await Make(timeout).ValidateAndCleanAsync(slow);
        slow.PosterUrl.Should().Be("https://slow.example.com/p.jpg");
    }

    [Fact]
    public async Task ACdnThatRefusesHead_IsCheckedWithGet()
    {
        var handler = new Handler(r => Status(r.Method == HttpMethod.Head ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.OK));
        var meta = new MediaMetadata { PosterUrl = "https://img.example.com/p.jpg" };

        await Make(handler).ValidateAndCleanAsync(meta);

        meta.PosterUrl.Should().NotBeNull();
        handler.Requests.Select(r => r.Method).Should().Equal(HttpMethod.Head, HttpMethod.Get);
    }

    [Fact]
    public async Task ASharedUrl_IsCheckedOnce_WhileTheAnswerIsRemembered()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new Handler(_ => Status(HttpStatusCode.OK));
        var validator = Make(handler, cache);

        await validator.ValidateAndCleanAsync(new MediaMetadata { PosterUrl = "https://img.example.com/p.jpg" });
        await validator.ValidateAndCleanAsync(new MediaMetadata { PosterUrl = "https://img.example.com/p.jpg" });

        handler.Requests.Count.Should().Be(1);
    }

    [Fact]
    public async Task AdditionalImages_AreCheckedOnce_ThumbnailsRideAlong_AndDroppedImagesTakeTheirThumbnailWithThem()
    {
        var handler = new Handler(r => Status(r.RequestUri!.AbsolutePath.Contains("gone") ? HttpStatusCode.NotFound : HttpStatusCode.OK));
        var meta = new MediaMetadata();
        meta.AdditionalImages.Add(new AdditionalImage { Url = "https://img.example.com/a.jpg", ThumbnailUrl = "https://img.example.com/a-t.jpg" });
        meta.AdditionalImages.Add(new AdditionalImage { Url = "https://img.example.com/gone.jpg", ThumbnailUrl = "https://img.example.com/gone-t.jpg" });

        await Make(handler).ValidateAndCleanAsync(meta);

        meta.AdditionalImages.Should().ContainSingle().Which.ThumbnailUrl.Should().Be("https://img.example.com/a-t.jpg");
        handler.Requests.Select(r => r.Url).Should().NotContain(u => u.Contains("-t.jpg"));
    }

    [Fact]
    public async Task ANonHttpUrl_IsInvalid()
    {
        var handler = new Handler(_ => Status(HttpStatusCode.OK));
        var meta = new MediaMetadata { PosterUrl = "ftp://x/p.jpg", BackdropUrl = "not a url" };

        await Make(handler).ValidateAndCleanAsync(meta);

        meta.PosterUrl.Should().BeNull();
        meta.BackdropUrl.Should().BeNull();
        handler.Requests.Should().BeEmpty();
    }
}
