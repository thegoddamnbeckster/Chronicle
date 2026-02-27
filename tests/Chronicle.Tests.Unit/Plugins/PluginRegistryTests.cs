using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services.Plugins;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Plugins;

/// <summary>
/// Tests for <see cref="PluginRegistry"/> using a lightweight in-process fake plugin
/// that implements <see cref="IMetadataProvider"/> directly — no DLL loading required.
/// </summary>
public class PluginRegistryTests : IDisposable
{
    private readonly PluginRegistry _registry = new();

    public void Dispose() => _registry.Dispose();

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Injects a fake LoadedPlugin directly into the registry by subclassing.
    /// Bypasses DLL loading for unit test purposes.
    /// </summary>
    private LoadedPlugin MakeFakeLoaded(int dbId, string pluginId)
    {
        var manifest = new PluginManifest
        {
            PluginId = pluginId,
            Name = $"Fake Plugin {pluginId}",
            Version = "1.0.0",
            Author = "Test"
        };
        var loadContext = new PluginLoadContext(typeof(PluginRegistryTests).Assembly.Location);
        var provider = new FakeMetadataProvider(pluginId);
        return new LoadedPlugin(loadContext, dbId, manifest,
            new List<IMetadataProvider> { provider },
            new List<IWidgetPlugin>());
    }

    // ── GetMetadataProviders ─────────────────────────────────────────────────

    [Fact]
    public void GetMetadataProviders_WhenEmpty_ReturnsEmptyList()
    {
        _registry.GetMetadataProviders().Should().BeEmpty();
    }

    [Fact]
    public void GetMetadataProvider_UnknownId_ReturnsNull()
    {
        _registry.GetMetadataProvider("unknown.plugin").Should().BeNull();
    }

    // ── GetLoadedPlugins ─────────────────────────────────────────────────────

    [Fact]
    public void GetLoadedPlugins_WhenEmpty_ReturnsEmptyList()
    {
        _registry.GetLoadedPlugins().Should().BeEmpty();
    }

    // ── UnloadPlugin ─────────────────────────────────────────────────────────

    [Fact]
    public void UnloadPlugin_NonExistentId_DoesNotThrow()
    {
        var act = () => _registry.UnloadPlugin(999);
        act.Should().NotThrow();
    }

    // ── PluginLoadContext (smoke test) ───────────────────────────────────────

    [Fact]
    public void PluginLoadContext_CanInstantiateWithValidAssemblyPath()
    {
        var assemblyPath = typeof(PluginRegistryTests).Assembly.Location;
        var act = () => new PluginLoadContext(assemblyPath);
        act.Should().NotThrow();
    }
}

// ── Fake plugin used by tests (no DLL needed) ────────────────────────────────

internal sealed class FakeMetadataProvider : IMetadataProvider
{
    public FakeMetadataProvider(string pluginId) => PluginId = pluginId;

    public string PluginId { get; }
    public string Name => "Fake";
    public string Version => "1.0.0";
    public string Author => "Test";

    public MediaTypeSupport[] GetSupportedMediaTypes() => [];
    public PluginSettingsSchema GetSettingsSchema() => new();
    public void Configure(IReadOnlyDictionary<string, string> settings) { }

    public Task<MediaMetadata> SearchAsync(string query, string mediaType, CancellationToken ct = default) =>
        Task.FromResult(new MediaMetadata { Title = "Fake Result" });

    public Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default) =>
        Task.FromResult(new MediaMetadata { ExternalId = externalId });

    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) =>
        Task.FromResult(true);
}
