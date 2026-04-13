using Chronicle.Services.Scan;

namespace Chronicle.Tests.Unit.Services;

public class BuiltInFileScannerPluginTests
{
    // ── MaxConcurrency schema ──────────────────────────────────────────────────

    [Fact]
    public void GetSettingsSchema_IncludesMaxConcurrencySetting()
    {
        var plugin = new BuiltInFileScannerPlugin();
        var schema = plugin.GetSettingsSchema();

        var setting = schema.Settings.FirstOrDefault(s => s.Key == "max_concurrency");
        Assert.NotNull(setting);
        Assert.Equal("max_concurrency", setting.Key);
    }

    [Fact]
    public void MaxConcurrency_DefaultsToZero_WhenNotConfigured()
    {
        var plugin = new BuiltInFileScannerPlugin();
        plugin.Configure(new Dictionary<string, string>());

        Assert.Equal(0, plugin.MaxConcurrency);
    }

    [Fact]
    public void MaxConcurrency_ReturnsConfiguredValue_WhenSet()
    {
        var plugin = new BuiltInFileScannerPlugin();
        plugin.Configure(new Dictionary<string, string>
        {
            ["max_concurrency"] = "4"
        });

        Assert.Equal(4, plugin.MaxConcurrency);
    }

    [Fact]
    public void MaxConcurrency_IgnoresInvalidValue_FallsBackToZero()
    {
        var plugin = new BuiltInFileScannerPlugin();
        plugin.Configure(new Dictionary<string, string>
        {
            ["max_concurrency"] = "not-a-number"
        });

        Assert.Equal(0, plugin.MaxConcurrency);
    }

    [Fact]
    public void MaxConcurrency_IgnoresZeroOrNegative_FallsBackToZero()
    {
        var plugin = new BuiltInFileScannerPlugin();
        plugin.Configure(new Dictionary<string, string>
        {
            ["max_concurrency"] = "0"
        });

        Assert.Equal(0, plugin.MaxConcurrency);
    }
}
