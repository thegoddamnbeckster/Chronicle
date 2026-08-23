using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration;

/// <summary>
/// A background task row belongs to a plugin forever once created -- disabling or uninstalling
/// the plugin doesn't clean these up. Reported by the user: after disabling Trakt, its Delta
/// Sync / Import All tasks (Run Now included) kept showing on the Background Tasks page.
/// </summary>
public class BackgroundTasksDisabledPluginTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    private const string AdminUser = "bgdisabled_admin_fixture";
    private const string AdminPass = "Password123!";

    public BackgroundTasksDisabledPluginTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
        EnsureAdminRegistered(factory).GetAwaiter().GetResult();
    }

    private static async Task EnsureAdminRegistered(ChronicleApiFactory factory)
    {
        var client = factory.CreateClient();
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
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private void SeedPluginWithTask(string pluginId, bool pluginEnabled, string taskId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        db.Plugins.Add(new Plugin
        {
            PluginId = pluginId, Name = pluginId, Version = "1.0.0", Author = "Test",
            DllPath = "test.dll", IsEnabled = pluginEnabled,
            InstalledAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.BackgroundTasks.Add(new BackgroundTask
        {
            TaskId = taskId, DisplayName = taskId, Description = "test task",
            CronExpression = "", PluginId = pluginId, Schedulable = false,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task GetAll_ExcludesTasksBelongingToADisabledPlugin()
    {
        var pluginId = $"chronicle.plugin.disabledtest.{Guid.NewGuid():N}";
        var taskId = $"{pluginId}:delta-sync";
        SeedPluginWithTask(pluginId, pluginEnabled: false, taskId);

        var client = await AdminClientAsync();
        var resp = await client.GetAsync("/api/v1/background-tasks");
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        var taskIds = doc.GetProperty("data").EnumerateArray()
            .Select(t => t.GetProperty("taskId").GetString()).ToList();
        taskIds.Should().NotContain(taskId);
    }

    [Fact]
    public async Task GetAll_StillIncludesTasksBelongingToAnEnabledPlugin()
    {
        var pluginId = $"chronicle.plugin.enabledtest.{Guid.NewGuid():N}";
        var taskId = $"{pluginId}:delta-sync";
        SeedPluginWithTask(pluginId, pluginEnabled: true, taskId);

        var client = await AdminClientAsync();
        var resp = await client.GetAsync("/api/v1/background-tasks");
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        var taskIds = doc.GetProperty("data").EnumerateArray()
            .Select(t => t.GetProperty("taskId").GetString()).ToList();
        taskIds.Should().Contain(taskId);
    }

    [Fact]
    public async Task GetAll_StillIncludesTasksWithNoOwningPlugin()
    {
        var taskId = $"core.{Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            db.BackgroundTasks.Add(new BackgroundTask
            {
                TaskId = taskId, DisplayName = taskId, Description = "core task, no plugin",
                CronExpression = "", PluginId = null, Schedulable = false,
            });
            db.SaveChanges();
        }

        var client = await AdminClientAsync();
        var resp = await client.GetAsync("/api/v1/background-tasks");
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        var taskIds = doc.GetProperty("data").EnumerateArray()
            .Select(t => t.GetProperty("taskId").GetString()).ToList();
        taskIds.Should().Contain(taskId);
    }
}
