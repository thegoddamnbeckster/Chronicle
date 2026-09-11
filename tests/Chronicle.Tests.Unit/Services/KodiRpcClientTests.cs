using System.Net;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronicle.Tests.Unit.Services;

public class KodiRpcClientTests
{
    private static readonly KodiDevice Device = new()
    {
        Id = 1, Name = "Shield", Host = "10.0.0.10", Port = 8080,
        LastSeenAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
    };

    // ── RefreshAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_Movie_SendsRefreshMovieWithMovieId()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":1,"result":"OK"}""");
        var client = new KodiRpcClient(new StubHttpClientFactory(handler), NullLogger<KodiRpcClient>.Instance);

        var ok = await client.RefreshAsync(Device, "movie", kodiId: 42);

        ok.Should().BeTrue();
        handler.LastRequestBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("method").GetString().Should().Be("VideoLibrary.RefreshMovie");
        doc.RootElement.GetProperty("params").GetProperty("movieid").GetInt32().Should().Be(42);
    }

    [Theory]
    [InlineData("tvshow", "VideoLibrary.RefreshTVShow", "tvshowid")]
    [InlineData("episode", "VideoLibrary.RefreshEpisode", "episodeid")]
    public async Task RefreshAsync_KnownKinds_SendCorrectMethodAndParamName(
        string kind, string expectedMethod, string expectedParamName)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":1,"result":"OK"}""");
        var client = new KodiRpcClient(new StubHttpClientFactory(handler), NullLogger<KodiRpcClient>.Instance);

        await client.RefreshAsync(Device, kind, kodiId: 7);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("method").GetString().Should().Be(expectedMethod);
        doc.RootElement.GetProperty("params").GetProperty(expectedParamName).GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task RefreshAsync_UnknownKind_ReturnsFalseWithoutSendingAnyRequest()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":1,"result":"OK"}""");
        var client = new KodiRpcClient(new StubHttpClientFactory(handler), NullLogger<KodiRpcClient>.Instance);

        var ok = await client.RefreshAsync(Device, "collection", kodiId: 1);

        ok.Should().BeFalse();
        handler.CallCount.Should().Be(0, "an unknown kind has no Kodi RPC method to call at all");
    }

    // ── ScanAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScanAsync_SendsVideoLibraryScanWithNoDirectory()
    {
        // No "directory" parameter -- see IKodiRpcClient.ScanAsync's own doc: Chronicle's
        // server-side folder path has no reliable mapping to how a given Kodi device names
        // that same share as one of its own sources, so this must always be a full scan.
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":1,"result":"OK"}""");
        var client = new KodiRpcClient(new StubHttpClientFactory(handler), NullLogger<KodiRpcClient>.Instance);

        var ok = await client.ScanAsync(Device);

        ok.Should().BeTrue();
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("method").GetString().Should().Be("VideoLibrary.Scan");
        doc.RootElement.GetProperty("params").TryGetProperty("directory", out _).Should().BeFalse();
        doc.RootElement.GetProperty("params").GetProperty("showdialogs").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ScanAsync_JsonRpcErrorResponse_ReturnsFalse()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"jsonrpc":"2.0","id":1,"error":{"code":-32602,"message":"Invalid params."}}""");
        var client = new KodiRpcClient(new StubHttpClientFactory(handler), NullLogger<KodiRpcClient>.Instance);

        var ok = await client.ScanAsync(Device);

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task ScanAsync_HttpFailureStatus_ReturnsFalseInsteadOfThrowing()
    {
        var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable, "");
        var client = new KodiRpcClient(new StubHttpClientFactory(handler), NullLogger<KodiRpcClient>.Instance);

        var act = async () => await client.ScanAsync(Device);

        (await act.Should().NotThrowAsync()).Which.Should().BeFalse();
    }

    [Fact]
    public async Task ScanAsync_DeviceUnreachable_ReturnsFalseInsteadOfThrowing()
    {
        var handler = new ThrowingHandler();
        var client = new KodiRpcClient(new StubHttpClientFactory(handler), NullLogger<KodiRpcClient>.Instance);

        var act = async () => await client.ScanAsync(Device);

        (await act.Should().NotThrowAsync()).Which.Should().BeFalse();
    }

    [Fact]
    public async Task ScanAsync_DeviceWithCredentials_SendsBasicAuthHeader()
    {
        var authedDevice = new KodiDevice
        {
            Id = 2, Name = "Shield", Host = "10.0.0.10", Port = 8080,
            Username = "kodi", Password = "1111",
            LastSeenAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
        };
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":1,"result":"OK"}""");
        var client = new KodiRpcClient(new StubHttpClientFactory(handler), NullLogger<KodiRpcClient>.Instance);

        await client.ScanAsync(authedDevice);

        handler.LastAuthHeader.Should().NotBeNull();
        handler.LastAuthHeader!.Scheme.Should().Be("Basic");
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }
        public System.Net.Http.Headers.AuthenticationHeaderValue? LastAuthHeader { get; private set; }
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            LastAuthHeader = request.Headers.Authorization;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }

    /// <summary>Simulates a device that's offline/unreachable -- SendAsync throws instead of
    /// returning a response, exercising KodiRpcClient's catch-and-return-false path (the
    /// same shape as a real connection-refused/timeout).</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("Connection refused");
    }
}
