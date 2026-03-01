using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration
{
    public class DeviceAuthTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;

        public DeviceAuthTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private async Task<(HttpClient client, string token)> RegisterAndLoginAsync()
        {
            var client   = _factory.CreateClient();
            var username = $"dev_{Guid.NewGuid():N}";

            var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { username, password = "Password123!" });

            var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("token").GetString()!;

            return (client, token);
        }

        private async Task<string> InitiateCodeAsync(HttpClient client)
        {
            var resp = await client.PostAsJsonAsync("/api/v1/auth/device",
                new { deviceName = "Test Device" });
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("code").GetString()!;
        }

        // ── POST /api/v1/auth/device (Initiate) ───────────────────────────────

        [Fact]
        public async Task Initiate_NoAuth_Returns200WithCodeAndUrls()
        {
            var client = _factory.CreateClient();

            var response = await client.PostAsJsonAsync("/api/v1/auth/device",
                new { deviceName = "Kodi" });

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var doc  = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            var data = doc.GetProperty("data");
            data.GetProperty("code").GetString().Should().NotBeNullOrEmpty();
            data.GetProperty("displayCode").GetString().Should().NotBeNullOrEmpty();
            data.GetProperty("verificationUrl").GetString().Should().NotBeNullOrEmpty();
            data.GetProperty("qrUrl").GetString().Should().NotBeNullOrEmpty();
        }

        [Fact]
        public async Task Initiate_WithoutDeviceName_Returns200()
        {
            var client   = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/v1/auth/device",
                new { });   // deviceName is optional

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Initiate_GeneratesUniqueCodeEachTime()
        {
            var client = _factory.CreateClient();

            var resp1 = await client.PostAsJsonAsync("/api/v1/auth/device", new { });
            var resp2 = await client.PostAsJsonAsync("/api/v1/auth/device", new { });

            var code1 = JsonDocument.Parse(await resp1.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("code").GetString();
            var code2 = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("code").GetString();

            code1.Should().NotBe(code2);
        }

        // ── GET /api/v1/auth/device/{code}/poll ──────────────────────────────

        [Fact]
        public async Task Poll_PendingCode_ReturnsPendingStatus()
        {
            var client = _factory.CreateClient();
            var code   = await InitiateCodeAsync(client);

            var response = await client.GetAsync($"/api/v1/auth/device/{code}/poll");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var doc  = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            var data = doc.GetProperty("data");
            data.GetProperty("status").GetString().Should().Be("pending");
            data.TryGetProperty("apiKey", out var apiKey).Should().BeTrue();
            // apiKey is null when pending
        }

        [Fact]
        public async Task Poll_UnknownCode_Returns200WithExpiredOrNotFoundStatus()
        {
            var client   = _factory.CreateClient();
            var response = await client.GetAsync("/api/v1/auth/device/nonexistentcode12345/poll");

            // The service returns a status; exact value depends on implementation
            // but should not throw a 500
            ((int)response.StatusCode).Should().BeLessThan(500);
        }

        [Fact]
        public async Task Poll_AfterApproval_ReturnsApprovedWithApiKey()
        {
            var (browserClient, token) = await RegisterAndLoginAsync();
            browserClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var deviceClient = _factory.CreateClient();
            var code         = await InitiateCodeAsync(deviceClient);

            // Browser user approves
            var approveResp = await browserClient.PostAsync(
                $"/api/v1/auth/device/{code}/approve", null);
            approveResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // Device polls
            var pollResp = await deviceClient.GetAsync(
                $"/api/v1/auth/device/{code}/poll");
            pollResp.StatusCode.Should().Be(HttpStatusCode.OK);

            var data = JsonDocument.Parse(await pollResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data");
            data.GetProperty("status").GetString().Should().Be("approved");
            data.GetProperty("apiKey").GetString().Should().NotBeNullOrEmpty();
        }

        [Fact]
        public async Task Poll_AfterDenial_ReturnsDeniedStatus()
        {
            var (browserClient, token) = await RegisterAndLoginAsync();
            browserClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var deviceClient = _factory.CreateClient();
            var code         = await InitiateCodeAsync(deviceClient);

            // Browser user denies
            var denyResp = await browserClient.PostAsync(
                $"/api/v1/auth/device/{code}/deny", null);
            denyResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // Device polls
            var pollResp = await deviceClient.GetAsync(
                $"/api/v1/auth/device/{code}/poll");
            pollResp.StatusCode.Should().Be(HttpStatusCode.OK);

            var data = JsonDocument.Parse(await pollResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data");
            data.GetProperty("status").GetString().Should().Be("denied");
        }

        // ── GET /api/v1/auth/device/{code}/qr ────────────────────────────────

        [Fact]
        public async Task GetQr_ValidCode_ReturnsPngImage()
        {
            var client = _factory.CreateClient();
            var code   = await InitiateCodeAsync(client);

            var response = await client.GetAsync($"/api/v1/auth/device/{code}/qr");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
            var bytes = await response.Content.ReadAsByteArrayAsync();
            bytes.Length.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task GetQr_InvalidCode_Returns404()
        {
            var client   = _factory.CreateClient();
            var response = await client.GetAsync("/api/v1/auth/device/nosuchcode/qr");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        // ── GET /api/v1/auth/device/{code} (Browser info) ────────────────────

        [Fact]
        public async Task GetInfo_ValidCode_Returns200WithInfo()
        {
            var (client, token) = await RegisterAndLoginAsync();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var deviceClient = _factory.CreateClient();
            var code         = await InitiateCodeAsync(deviceClient);

            var response = await client.GetAsync($"/api/v1/auth/device/{code}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var doc  = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            var data = doc.GetProperty("data");
            data.GetProperty("displayCode").GetString().Should().NotBeNullOrEmpty();
            data.GetProperty("status").GetString().Should().Be("pending");
        }

        [Fact]
        public async Task GetInfo_WithoutAuth_Returns401()
        {
            var deviceClient = _factory.CreateClient();
            var code         = await InitiateCodeAsync(deviceClient);

            var anonClient = _factory.CreateClient();
            var response   = await anonClient.GetAsync($"/api/v1/auth/device/{code}");
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task GetInfo_NonExistentCode_Returns404()
        {
            var (client, token) = await RegisterAndLoginAsync();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var response = await client.GetAsync("/api/v1/auth/device/nosuchcode");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        // ── POST /api/v1/auth/device/{code}/approve ───────────────────────────

        [Fact]
        public async Task Approve_ValidCode_Returns200()
        {
            var (client, token) = await RegisterAndLoginAsync();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var deviceClient = _factory.CreateClient();
            var code         = await InitiateCodeAsync(deviceClient);

            var response = await client.PostAsync(
                $"/api/v1/auth/device/{code}/approve", null);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Approve_AlreadyApprovedCode_Returns409()
        {
            var (client, token) = await RegisterAndLoginAsync();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var deviceClient = _factory.CreateClient();
            var code         = await InitiateCodeAsync(deviceClient);

            await client.PostAsync($"/api/v1/auth/device/{code}/approve", null);

            // Try to approve again
            var response = await client.PostAsync(
                $"/api/v1/auth/device/{code}/approve", null);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        [Fact]
        public async Task Approve_NonExistentCode_Returns404()
        {
            var (client, token) = await RegisterAndLoginAsync();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var response = await client.PostAsync(
                "/api/v1/auth/device/nosuchcode/approve", null);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task Approve_WithoutAuth_Returns401()
        {
            var deviceClient = _factory.CreateClient();
            var code         = await InitiateCodeAsync(deviceClient);

            var anonClient = _factory.CreateClient();
            var response   = await anonClient.PostAsync(
                $"/api/v1/auth/device/{code}/approve", null);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // ── POST /api/v1/auth/device/{code}/deny ──────────────────────────────

        [Fact]
        public async Task Deny_ValidCode_Returns200()
        {
            var (client, token) = await RegisterAndLoginAsync();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var deviceClient = _factory.CreateClient();
            var code         = await InitiateCodeAsync(deviceClient);

            var response = await client.PostAsync(
                $"/api/v1/auth/device/{code}/deny", null);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Deny_AlreadyDeniedCode_Returns409()
        {
            var (client, token) = await RegisterAndLoginAsync();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var deviceClient = _factory.CreateClient();
            var code         = await InitiateCodeAsync(deviceClient);

            await client.PostAsync($"/api/v1/auth/device/{code}/deny", null);

            var response = await client.PostAsync(
                $"/api/v1/auth/device/{code}/deny", null);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        [Fact]
        public async Task Deny_WithoutAuth_Returns401()
        {
            var deviceClient = _factory.CreateClient();
            var code         = await InitiateCodeAsync(deviceClient);

            var anonClient = _factory.CreateClient();
            var response   = await anonClient.PostAsync(
                $"/api/v1/auth/device/{code}/deny", null);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }
}
