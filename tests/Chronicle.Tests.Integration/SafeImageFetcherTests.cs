using System.Net;
using System.Net.Sockets;
using Chronicle.API.Helpers;
using FluentAssertions;

namespace Chronicle.Tests.Integration
{
    /// <summary>
    /// The network behaviour of the poster proxy's fetcher, against a real local HTTP server.
    /// The production filter (public addresses only) would refuse a loopback server, so these
    /// tests inject a permissive filter that still refuses one specific "private" address -
    /// which is enough to prove the filter is applied to the connection actually made, including
    /// redirect targets and DNS answers.
    /// </summary>
    public sealed class SafeImageFetcherTests : IDisposable
    {
        private static readonly IPAddress ForbiddenAddress = IPAddress.Parse("127.0.0.2");
        private static readonly byte[] TinyJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xD9];

        private readonly HttpListener _listener = new();
        private readonly int _port;
        private readonly CancellationTokenSource _stop = new();
        private readonly SafeImageFetcher _fetcher;

        public SafeImageFetcherTests()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            _ = Task.Run(ServeAsync);

            _fetcher = new SafeImageFetcher(address => !address.Equals(ForbiddenAddress));
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }

        private string Url(string path) => $"http://127.0.0.1:{_port}{path}";

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                try
                {
                    var res = ctx.Response;
                    switch (ctx.Request.Url!.AbsolutePath)
                    {
                        case "/ok.jpg":
                            res.ContentType = "image/jpeg";
                            await res.OutputStream.WriteAsync(TinyJpeg);
                            break;
                        case "/ok-jpg-alias":
                            res.ContentType = "image/jpg";
                            await res.OutputStream.WriteAsync(TinyJpeg);
                            break;
                        case "/image.svg":
                            res.ContentType = "image/svg+xml";
                            await res.OutputStream.WriteAsync("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8.ToArray());
                            break;
                        case "/page.html":
                            res.ContentType = "text/html";
                            await res.OutputStream.WriteAsync("<html></html>"u8.ToArray());
                            break;
                        case "/no-type":
                            await res.OutputStream.WriteAsync(TinyJpeg);
                            break;
                        case "/big-with-length":
                            res.ContentType = "image/png";
                            res.ContentLength64 = SafeImageFetcher.MaxBytes + 1;
                            await res.OutputStream.WriteAsync(new byte[1024]);   // client gives up on the header alone
                            break;
                        case "/big-chunked":
                            res.ContentType = "image/png";
                            res.SendChunked = true;
                            var chunk = new byte[1024 * 1024];
                            for (var i = 0; i < 11; i++) await res.OutputStream.WriteAsync(chunk);
                            break;
                        case "/redirect-ok":
                            res.StatusCode = 302;
                            res.RedirectLocation = Url("/ok.jpg");
                            break;
                        case "/redirect-forbidden":
                            res.StatusCode = 302;
                            res.RedirectLocation = $"http://127.0.0.2:{_port}/ok.jpg";
                            break;
                        case "/redirect-loop":
                            res.StatusCode = 302;
                            res.RedirectLocation = Url("/redirect-loop");
                            break;
                        default:
                            res.StatusCode = 404;
                            break;
                    }
                    res.Close();
                }
                catch { /* the client hung up (expected for the oversize cases) */ }
            }
        }

        [Fact]
        public async Task AnImage_IsReturnedWithItsType()
        {
            var result = await _fetcher.FetchAsync(Url("/ok.jpg"), CancellationToken.None);

            result.Status.Should().Be(SafeImageFetcher.FetchStatus.Ok);
            result.ContentType.Should().Be("image/jpeg");
            result.Bytes.Should().Equal(TinyJpeg);
        }

        [Fact]
        public async Task TheNonStandardImageJpgType_IsAccepted()
        {
            (await _fetcher.FetchAsync(Url("/ok-jpg-alias"), CancellationToken.None)).Status
                .Should().Be(SafeImageFetcher.FetchStatus.Ok);
        }

        [Theory]
        [InlineData("/image.svg")]    // can carry script when opened directly
        [InlineData("/page.html")]
        [InlineData("/no-type")]      // no Content-Type at all: not trusted as an image
        public async Task NonRasterContent_IsRefused(string path)
        {
            (await _fetcher.FetchAsync(Url(path), CancellationToken.None)).Status
                .Should().Be(SafeImageFetcher.FetchStatus.UnsupportedType);
        }

        [Fact]
        public async Task AnOversizeBody_IsRefused_WhetherDeclaredOrStreamed()
        {
            (await _fetcher.FetchAsync(Url("/big-with-length"), CancellationToken.None)).Status
                .Should().Be(SafeImageFetcher.FetchStatus.TooLarge);
            (await _fetcher.FetchAsync(Url("/big-chunked"), CancellationToken.None)).Status
                .Should().Be(SafeImageFetcher.FetchStatus.TooLarge, "no Content-Length does not get around the cap");
        }

        [Fact]
        public async Task ARedirectToAnAllowedAddress_IsFollowed()
        {
            (await _fetcher.FetchAsync(Url("/redirect-ok"), CancellationToken.None)).Status
                .Should().Be(SafeImageFetcher.FetchStatus.Ok);
        }

        [Fact]
        public async Task ARedirectToAForbiddenAddress_IsRefused_BeforeAnyConnectionIsMade()
        {
            // The first hop is fine; the Location points at the forbidden address. Without a
            // check on the connection actually opened, a public URL could bounce the server
            // onto its own network.
            (await _fetcher.FetchAsync(Url("/redirect-forbidden"), CancellationToken.None)).Status
                .Should().Be(SafeImageFetcher.FetchStatus.NotFound);
        }

        [Fact]
        public async Task ARedirectLoop_GivesUp()
        {
            (await _fetcher.FetchAsync(Url("/redirect-loop"), CancellationToken.None)).Status
                .Should().Be(SafeImageFetcher.FetchStatus.NotFound);
        }

        [Fact]
        public async Task ANotFound_IsNotFound()
        {
            (await _fetcher.FetchAsync(Url("/missing"), CancellationToken.None)).Status
                .Should().Be(SafeImageFetcher.FetchStatus.NotFound);
        }

        [Fact]
        public async Task ADirectRequestForTheForbiddenAddress_IsRefused()
        {
            (await _fetcher.FetchAsync($"http://127.0.0.2:{_port}/ok.jpg", CancellationToken.None)).Status
                .Should().Be(SafeImageFetcher.FetchStatus.NotFound);
        }

        // ── per-call options (the plugin icon route uses these) ───────────────

        [Fact]
        public async Task HttpsOnly_RefusesAPlainHttpUrl()
        {
            var result = await _fetcher.FetchAsync(Url("/ok.jpg"), CancellationToken.None,
                new SafeImageFetcher.FetchOptions(HttpsOnly: true));

            result.Status.Should().Be(SafeImageFetcher.FetchStatus.BadRequest);
        }

        [Fact]
        public async Task ExtraContentTypes_LetACallerAcceptSvg_WhichIsOtherwiseRefused()
        {
            var options = new SafeImageFetcher.FetchOptions(ExtraContentTypes: ["image/svg+xml"]);

            (await _fetcher.FetchAsync(Url("/image.svg"), CancellationToken.None, options)).Status
                .Should().Be(SafeImageFetcher.FetchStatus.Ok);
            (await _fetcher.FetchAsync(Url("/image.svg"), CancellationToken.None)).Status
                .Should().Be(SafeImageFetcher.FetchStatus.UnsupportedType, "the default stays raster-only");
        }

        [Fact]
        public async Task ASmallerSizeLimit_IsHonoured_ForDeclaredAndStreamedBodies()
        {
            var tiny = new SafeImageFetcher.FetchOptions(MaxBytes: 10);

            (await _fetcher.FetchAsync(Url("/ok.jpg"), CancellationToken.None, tiny)).Status
                .Should().Be(SafeImageFetcher.FetchStatus.TooLarge, "the sample image is 22 bytes");
            (await _fetcher.FetchAsync(Url("/ok.jpg"), CancellationToken.None,
                new SafeImageFetcher.FetchOptions(MaxBytes: 1000))).Status
                .Should().Be(SafeImageFetcher.FetchStatus.Ok);
        }

        // ── DNS: judged on every address the name resolves to ─────────────────

        [Fact]
        public async Task AHostnameResolvingToAMixOfPublicAndPrivate_IsRefusedOutright()
        {
            var fetcher = new SafeImageFetcher(SafeImageFetcher.IsPublicAddress,
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.5") }));

            (await fetcher.FetchAsync("http://mixed.example/a.jpg", CancellationToken.None)).Status
                .Should().Be(SafeImageFetcher.FetchStatus.NotFound);
        }

        [Fact]
        public async Task AHostnameResolvingToLoopback_IsRefused_TheDnsRebindingCase()
        {
            var fetcher = new SafeImageFetcher(SafeImageFetcher.IsPublicAddress,
                (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));

            (await fetcher.FetchAsync($"http://rebind.example:{_port}/ok.jpg", CancellationToken.None)).Status
                .Should().Be(SafeImageFetcher.FetchStatus.NotFound, "the server is really listening there, but it must not be reached");
        }

        [Fact]
        public async Task ANameThatResolvesToNothing_IsRefused()
        {
            var fetcher = new SafeImageFetcher(SafeImageFetcher.IsPublicAddress,
                (_, _) => Task.FromResult(Array.Empty<IPAddress>()));

            (await fetcher.FetchAsync("http://nowhere.example/a.jpg", CancellationToken.None)).Status
                .Should().Be(SafeImageFetcher.FetchStatus.NotFound);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not a url")]
        [InlineData("ftp://example.com/a.jpg")]
        [InlineData("file:///c:/windows/win.ini")]
        [InlineData("http://user:pw@example.com/a.jpg")]
        public async Task MalformedOrNonHttpUrls_AreBadRequests(string url)
        {
            (await _fetcher.FetchAsync(url, CancellationToken.None)).Status
                .Should().Be(SafeImageFetcher.FetchStatus.BadRequest);
        }
    }
}
