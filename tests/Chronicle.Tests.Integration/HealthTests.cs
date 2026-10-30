using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration
{
    public class HealthTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly HttpClient _client;

        public HealthTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _client = factory.CreateClient();
        }

        [Fact]
        public async Task DeepHealth_AnswersAnonymously_WithStatusAndChecks()
        {
            var response = await _client.GetAsync("/api/health/deep");

            response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("status").GetString().Should().BeOneOf("healthy", "unhealthy");
            body.TryGetProperty("checks", out _).Should().BeTrue();
            (response.StatusCode == HttpStatusCode.OK).Should().Be(body.GetProperty("status").GetString() == "healthy");
        }

        [Fact]
        public async Task DeepHealth_DoesNotExposeRowCounts()
        {
            var text = await (await _client.GetAsync("/api/health/deep")).Content.ReadAsStringAsync();

            text.ToLowerInvariant().Should().NotContain("mediaitems").And.NotContain("users\":");
        }

        [Theory]
        [InlineData("GET")]
        [InlineData("PUT")]
        public async Task WatchdogSettings_RequireAnAdministratorSession(string method)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), "/api/v1/settings/watchdog");
            if (method == "PUT") request.Content = JsonContent.Create(new { enabled = true });

            var response = await _client.SendAsync(request);

            response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        }
    }
}
