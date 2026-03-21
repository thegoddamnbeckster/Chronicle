using System.Text.Json;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Xunit;

namespace Chronicle.Tests.Unit.Plugins;

public class PluginManifestTests
{
    [Fact]
    public void PluginManifest_DeserializesBackgroundTasksAndBranding()
    {
        var json = """
        {
          "plugin_id": "test.plugin",
          "name": "Test",
          "version": "1.0.0",
          "author": "Test",
          "min_chronicle_version": "0.1.0",
          "entry_type": "Test.Plugin",
          "iconUrl": "https://example.com/favicon.ico",
          "brandColorLight": "#BA478F",
          "brandColorDark": "#CF6BAA",
          "background_tasks": [
            {
              "task_id": "fetch-missing-metadata",
              "display_name": "Fetch Missing Metadata",
              "description": "Looks up metadata for new items.",
              "default_cron": "0 4 * * *",
              "default_enabled": true
            }
          ]
        }
        """;

        var manifest = JsonSerializer.Deserialize<PluginManifest>(json);

        Assert.Equal("#BA478F", manifest!.BrandColorLight);
        Assert.Equal("#CF6BAA", manifest.BrandColorDark);
        Assert.Single(manifest.BackgroundTasks!);
        Assert.Equal("fetch-missing-metadata", manifest.BackgroundTasks![0].TaskId);
        Assert.Equal("Fetch Missing Metadata", manifest.BackgroundTasks[0].DisplayName);
        Assert.Equal("0 4 * * *", manifest.BackgroundTasks[0].DefaultCron);
        Assert.True(manifest.BackgroundTasks[0].DefaultEnabled);
    }

    [Fact]
    public void IPluginTask_CanBeImplementedExternally()
    {
        var type = typeof(IPluginTask);
        Assert.True(type.IsInterface);
        Assert.True(type.IsPublic);
        Assert.NotNull(type.GetProperty("TaskId"));
        Assert.NotNull(type.GetMethod("RunAsync"));
    }
}
