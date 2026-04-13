using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration;

public class BackgroundTasksTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    // Fixed admin credentials — first registration in this factory instance gets Admin role.
    private const string AdminUser = "bg_admin_fixture";
    private const string AdminPass = "Password123!";

    public BackgroundTasksTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;

        // Ensure the admin user exists (first call creates it as admin; subsequent calls are no-ops).
        EnsureAdminRegistered(factory).GetAwaiter().GetResult();
    }

    private static async Task EnsureAdminRegistered(ChronicleApiFactory factory)
    {
        var client = factory.CreateClient();
        // Attempt registration — succeeds the first time (first user = admin), ignored after.
        await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = AdminUser, password = AdminPass });
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = AdminUser, password = AdminPass });
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task GetTasks_Authenticated_ReturnsTaskList()
    {
        var client = await AdminClientAsync();

        var resp = await client.GetAsync("/api/v1/background-tasks");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("success").GetBoolean().Should().BeTrue();

        var tasks = doc.GetProperty("data");
        tasks.GetArrayLength().Should().BeGreaterThan(0);

        var first = tasks[0];
        first.TryGetProperty("taskId", out _).Should().BeTrue();
        first.TryGetProperty("displayName", out _).Should().BeTrue();
        first.TryGetProperty("cronExpression", out _).Should().BeTrue();
        first.TryGetProperty("isEnabled", out _).Should().BeTrue();
        first.TryGetProperty("isRunning", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetTasks_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/background-tasks");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PatchTask_ValidCron_PersistsChange()
    {
        var client = await AdminClientAsync();

        var listResp = await client.GetAsync("/api/v1/background-tasks");
        var tasks = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data");
        var taskId = tasks[0].GetProperty("taskId").GetString()!;

        var patchResp = await client.PatchAsJsonAsync(
            $"/api/v1/background-tasks/{taskId}",
            new { cronExpression = "0 2 * * *", isEnabled = true });

        patchResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResp = await client.GetAsync("/api/v1/background-tasks");
        var updated = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data")
            .EnumerateArray()
            .First(t => t.GetProperty("taskId").GetString() == taskId);

        updated.GetProperty("cronExpression").GetString().Should().Be("0 2 * * *");
    }

    [Fact]
    public async Task PatchTask_InvalidCron_Returns400WithFriendlyMessage()
    {
        var client = await AdminClientAsync();

        var listResp = await client.GetAsync("/api/v1/background-tasks");
        var taskId = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data")[0].GetProperty("taskId").GetString()!;

        var resp = await client.PatchAsJsonAsync(
            $"/api/v1/background-tasks/{taskId}",
            new { cronExpression = "not a cron" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        var msg = body.GetProperty("error").GetProperty("message").GetString()!;
        msg.Should().Contain("cron expression");
    }

    [Fact]
    public async Task PatchTask_UnknownId_Returns404()
    {
        var client = await AdminClientAsync();
        var resp = await client.PatchAsJsonAsync(
            "/api/v1/background-tasks/does_not_exist",
            new { isEnabled = false });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RunTask_ValidId_Returns202()
    {
        var client = await AdminClientAsync();

        var listResp = await client.GetAsync("/api/v1/background-tasks");
        var taskId = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data")[0].GetProperty("taskId").GetString()!;

        var resp = await client.PostAsync($"/api/v1/background-tasks/{taskId}/run", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task RunTask_UnknownId_Returns404()
    {
        var client = await AdminClientAsync();
        var resp = await client.PostAsync("/api/v1/background-tasks/ghost_task/run", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAll_ReturnsSchedulableAndRunConfirmation_Fields()
    {
        var client = await AdminClientAsync();

        // Seed a task with the new fields directly in the test DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        db.BackgroundTasks.Add(new Chronicle.Core.Models.BackgroundTask
        {
            TaskId                 = "test:confirm-task",
            DisplayName            = "Test",
            Description            = "Test task",
            CronExpression         = string.Empty,
            Schedulable            = false,
            RunConfirmationTitle   = "Sure?",
            RunConfirmationMessage = "Body text.",
        });
        await db.SaveChangesAsync();

        var resp = await client.GetAsync("/api/v1/background-tasks");
        resp.EnsureSuccessStatusCode();

        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var task = json.RootElement.GetProperty("data")
            .EnumerateArray()
            .First(t => t.GetProperty("taskId").GetString() == "test:confirm-task");

        Assert.False(task.GetProperty("schedulable").GetBoolean());
        Assert.Equal("Sure?",      task.GetProperty("runConfirmation").GetProperty("title").GetString());
        Assert.Equal("Body text.", task.GetProperty("runConfirmation").GetProperty("message").GetString());
    }
}
