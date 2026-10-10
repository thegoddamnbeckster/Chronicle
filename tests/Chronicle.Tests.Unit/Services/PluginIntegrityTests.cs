using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Notifications;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Moq;

namespace Chronicle.Tests.Unit.Services;

public sealed class PluginIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronicle-integrity-" + Guid.NewGuid().ToString("N"));
    private readonly ChronicleDbContext _db = new(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<INotificationService> _notify = new();

    public PluginIntegrityTests() => Directory.CreateDirectory(Path.Combine(_root, "plugins"));
    public void Dispose() { _db.Dispose(); try { Directory.Delete(_root, true); } catch { /* scratch */ } }

    private PluginIntegrity Sut(bool withNotifications = true)
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(_root);
        return new PluginIntegrity(_db, env.Object, withNotifications ? _notify.Object : null);
    }

    private string MakePlugin(string folder, string dllText = "dll-v1", string manifest = "{}")
    {
        var dir = Path.Combine(_root, "plugins", folder);
        Directory.CreateDirectory(dir);
        var dll = Path.Combine(dir, folder + ".dll");
        File.WriteAllText(dll, dllText);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest);
        return dll;
    }

    private Plugin Row(string dll, string id = "chronicle.plugin.tmdb") => new()
    {
        PluginId = id, Name = "TMDB", Version = "1", Author = "a", DllPath = dll, InstalledAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    // ── the hash ───────────────────────────────────────────────────────────

    [Fact]
    public void Hash_IsStable_AndChangesWithAnyFile()
    {
        var dll = MakePlugin("p");
        var first = PluginIntegrity.ComputeFilesHash(dll);
        PluginIntegrity.ComputeFilesHash(dll).Should().Be(first);

        File.WriteAllText(dll, "dll-v2");
        var afterDll = PluginIntegrity.ComputeFilesHash(dll);
        afterDll.Should().NotBe(first);

        File.WriteAllText(Path.Combine(Path.GetDirectoryName(dll)!, "manifest.json"), "{\"changed\":1}");
        var afterManifest = PluginIntegrity.ComputeFilesHash(dll);
        afterManifest.Should().NotBe(afterDll);

        File.WriteAllText(Path.Combine(Path.GetDirectoryName(dll)!, "Dependency.dll"), "extra");
        PluginIntegrity.ComputeFilesHash(dll).Should().NotBe(afterManifest);   // a dropped-in dependency counts
    }

    [Fact]
    public void Hash_ComesOutTheSameForTheSameContentInAnotherFolder()
    {
        var a = PluginIntegrity.ComputeFilesHash(MakePlugin("a", "same"));
        var b = PluginIntegrity.ComputeFilesHash(MakePlugin("a", "same"));
        a.Should().Be(b);
        a.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Hash_IgnoresLooseFilesThatAreNotCode()
    {
        var dll = MakePlugin("p");
        var before = PluginIntegrity.ComputeFilesHash(dll);

        File.WriteAllText(Path.Combine(Path.GetDirectoryName(dll)!, "notes.txt"), "hello");

        PluginIntegrity.ComputeFilesHash(dll).Should().Be(before);
    }

    // ── verify ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task APluginWithNoHashOnRecord_HasItRecorded_AndLoads()
    {
        var plugin = Row(MakePlugin("p"));
        _db.Plugins.Add(plugin); await _db.SaveChangesAsync();

        (await Sut().VerifyAsync(plugin)).Should().Be(IntegrityOutcome.Recorded);

        plugin.FilesSha256.Should().NotBeNullOrEmpty();
        (await Sut().VerifyAsync(plugin)).Should().Be(IntegrityOutcome.Verified);
    }

    [Fact]
    public async Task ChangedFiles_AreBlocked_AdministratorsAreTold_AndTheRecordedHashIsKept()
    {
        var dll = MakePlugin("p");
        var plugin = Row(dll);
        _db.Plugins.Add(plugin); await _db.SaveChangesAsync();
        await Sut().AcceptCurrentFilesAsync(plugin);
        var trusted = plugin.FilesSha256;

        File.WriteAllText(dll, "tampered");
        var outcome = await Sut().VerifyAsync(plugin);

        outcome.Should().Be(IntegrityOutcome.Blocked);
        plugin.IntegrityBlockedAt.Should().NotBeNull();
        plugin.FilesSha256.Should().Be(trusted);
        _notify.Verify(n => n.NotifyAdminsAsync(NotificationKinds.PluginIntegrity, It.IsAny<string>(), It.IsAny<string?>(),
            "/plugins", "plugin.integrity:chronicle.plugin.tmdb", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ABlockedPlugin_IsNotAnnouncedAgainOnEveryAttempt()
    {
        var dll = MakePlugin("p");
        var plugin = Row(dll);
        _db.Plugins.Add(plugin); await _db.SaveChangesAsync();
        await Sut().AcceptCurrentFilesAsync(plugin);
        File.WriteAllText(dll, "tampered");

        await Sut().VerifyAsync(plugin);
        await Sut().VerifyAsync(plugin);
        await Sut().VerifyAsync(plugin);

        _notify.Verify(n => n.NotifyAdminsAsync(NotificationKinds.PluginIntegrity, It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PuttingTheOriginalFilesBack_ClearsTheBlock()
    {
        var dll = MakePlugin("p");
        var plugin = Row(dll);
        _db.Plugins.Add(plugin); await _db.SaveChangesAsync();
        await Sut().AcceptCurrentFilesAsync(plugin);
        File.WriteAllText(dll, "tampered");
        await Sut().VerifyAsync(plugin);

        File.WriteAllText(dll, "dll-v1");

        (await Sut().VerifyAsync(plugin)).Should().Be(IntegrityOutcome.Verified);
        plugin.IntegrityBlockedAt.Should().BeNull();
    }

    [Fact]
    public async Task Verify_WorksWithoutANotificationService()
    {
        var dll = MakePlugin("p");
        var plugin = Row(dll);
        _db.Plugins.Add(plugin); await _db.SaveChangesAsync();
        await Sut(withNotifications: false).AcceptCurrentFilesAsync(plugin);
        File.WriteAllText(dll, "tampered");

        (await Sut(withNotifications: false).VerifyAsync(plugin)).Should().Be(IntegrityOutcome.Blocked);
    }

    [Fact]
    public async Task AMissingDll_IsLeftToTheLoaderToReport()
    {
        var plugin = Row(Path.Combine(_root, "plugins", "gone", "gone.dll"));
        _db.Plugins.Add(plugin); await _db.SaveChangesAsync();

        (await Sut().VerifyAsync(plugin)).Should().Be(IntegrityOutcome.Verified);
    }

    // ── accepting ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Accepting_RecordsTheNewHash_KeepsTheOldOne_AndUnblocks()
    {
        var dll = MakePlugin("p");
        var plugin = Row(dll);
        _db.Plugins.Add(plugin); await _db.SaveChangesAsync();
        await Sut().AcceptCurrentFilesAsync(plugin);
        var first = plugin.FilesSha256;
        File.WriteAllText(dll, "update");
        await Sut().VerifyAsync(plugin);

        await Sut().AcceptCurrentFilesAsync(plugin);

        plugin.FilesSha256.Should().NotBe(first);
        plugin.PreviousFilesSha256.Should().Be(first);
        plugin.FilesChangedAt.Should().NotBeNull();
        plugin.IntegrityBlockedAt.Should().BeNull();
        (await Sut().VerifyAsync(plugin)).Should().Be(IntegrityOutcome.Verified);
    }

    [Fact]
    public async Task Accepting_UnchangedFiles_ChangesNothing()
    {
        var plugin = Row(MakePlugin("p"));
        _db.Plugins.Add(plugin); await _db.SaveChangesAsync();
        await Sut().AcceptCurrentFilesAsync(plugin);
        var changedAt = plugin.FilesChangedAt;

        await Sut().AcceptCurrentFilesAsync(plugin);

        plugin.PreviousFilesSha256.Should().BeNull();
        plugin.FilesChangedAt.Should().Be(changedAt);
    }

    // ── what may be installed ──────────────────────────────────────────────

    [Fact]
    public async Task CatalogPlugins_AreAllowed_OthersAreNot()
    {
        (await Sut().IsAllowedAsync("chronicle.plugin.tmdb")).Should().BeTrue();
        (await Sut().IsAllowedAsync("CHRONICLE.PLUGIN.TMDB")).Should().BeTrue();
        (await Sut().IsAllowedAsync("evil.plugin")).Should().BeFalse();
    }

    [Fact]
    public async Task AnAdministrator_CanAllowUnlistedPlugins()
    {
        _db.AppSettings.Add(new AppSetting { Key = PluginIntegrity.AllowUnlistedKey, Value = "true" });
        await _db.SaveChangesAsync();

        (await Sut().IsAllowedAsync("my.own.plugin")).Should().BeTrue();
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("yes")]
    public async Task OnlyTrueSwitchesItOn(string value)
    {
        _db.AppSettings.Add(new AppSetting { Key = PluginIntegrity.AllowUnlistedKey, Value = value });
        await _db.SaveChangesAsync();

        (await Sut().IsAllowedAsync("my.own.plugin")).Should().BeFalse();
    }

    [Fact]
    public void OnlyPathsInsideThePluginsFolder_Count()
    {
        var sut = Sut();

        sut.IsInsidePluginsDirectory(Path.Combine(_root, "plugins", "x", "x.dll")).Should().BeTrue();
        sut.IsInsidePluginsDirectory(Path.Combine(_root, "plugins", "..", "evil.dll")).Should().BeFalse();
        sut.IsInsidePluginsDirectory(Path.Combine(_root, "pluginsEvil", "x.dll")).Should().BeFalse();
        sut.IsInsidePluginsDirectory(Path.Combine(Path.GetTempPath(), "x.dll")).Should().BeFalse();
        sut.IsInsidePluginsDirectory(Path.Combine(_root, "plugins")).Should().BeFalse();
    }

    // ── the install guard in PluginService ─────────────────────────────────

    private PluginService Service()
    {
        var registry = new Mock<IPluginRegistry>(MockBehavior.Strict);   // a refused plugin must never reach the loader
        var protector = new Mock<IPluginSettingsProtector>();
        return new PluginService(_db, registry.Object, protector.Object, Sut());
    }

    [Fact]
    public async Task ManualInstall_FromOutsideThePluginsFolder_IsRefusedBeforeAnythingLoads()
    {
        var outside = Path.Combine(Path.GetTempPath(), "chronicle-outside-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllText(outside, "x");
        try
        {
            var act = () => Service().InstallPluginAsync(outside);
            (await act.Should().ThrowAsync<PluginNotAllowedException>()).Which.Message.Should().Contain("plugins folder");
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task ManualInstall_OfAnUnlistedPlugin_IsRefusedBeforeAnythingLoads()
    {
        var dll = MakePlugin("sneaky", manifest: "{\"plugin_id\":\"evil.plugin\",\"name\":\"Evil\"}");

        var act = () => Service().InstallPluginAsync(dll);

        (await act.Should().ThrowAsync<PluginNotAllowedException>()).Which.Message.Should().Contain("evil.plugin").And.Contain("not in the plugin catalog");
    }

    [Fact]
    public async Task ManualInstall_WithoutAManifest_IsRefused()
    {
        var dll = MakePlugin("nomanifest");
        File.Delete(Path.Combine(Path.GetDirectoryName(dll)!, "manifest.json"));

        var act = () => Service().InstallPluginAsync(dll);

        (await act.Should().ThrowAsync<PluginNotAllowedException>()).Which.Message.Should().Contain("manifest.json");
    }

    [Fact]
    public async Task CatalogInstall_WhoseFilesDeclareADifferentPlugin_IsRefused()
    {
        var dll = MakePlugin("swap", manifest: "{\"plugin_id\":\"chronicle.plugin.trakt\"}");

        var act = () => Service().InstallPluginAsync(dll, default, catalogPluginId: "chronicle.plugin.tmdb");

        (await act.Should().ThrowAsync<PluginNotAllowedException>()).Which.Message.Should().Contain("chronicle.plugin.trakt");
    }

    [Fact]
    public async Task AcceptingFiles_ThatDeclareADifferentPlugin_IsRefused_AndTheOldHashStays()
    {
        var dll = MakePlugin("p", manifest: "{\"plugin_id\":\"chronicle.plugin.tmdb\"}");
        var plugin = Row(dll);
        _db.Plugins.Add(plugin); await _db.SaveChangesAsync();
        await Service().AcceptCurrentFilesAsync(plugin.PluginId);
        var trusted = plugin.FilesSha256;
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(dll)!, "manifest.json"), "{\"plugin_id\":\"chronicle.plugin.trakt\"}");

        var act = () => Service().AcceptCurrentFilesAsync(plugin.PluginId);

        (await act.Should().ThrowAsync<PluginNotAllowedException>()).Which.Message.Should().Contain("chronicle.plugin.trakt");
        plugin.FilesSha256.Should().Be(trusted);
    }

    [Fact]
    public async Task AcceptingFiles_WhoseManifestMatches_Works()
    {
        var dll = MakePlugin("p", manifest: "{\"plugin_id\":\"chronicle.plugin.tmdb\"}");
        var plugin = Row(dll);
        _db.Plugins.Add(plugin); await _db.SaveChangesAsync();

        await Service().AcceptCurrentFilesAsync(plugin.PluginId);

        plugin.FilesSha256.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task EnablingAPluginWhoseFilesChanged_IsRefused_AndNeverLoaded()
    {
        var dll = MakePlugin("p");
        var plugin = Row(dll);
        _db.Plugins.Add(plugin); await _db.SaveChangesAsync();
        await Sut().AcceptCurrentFilesAsync(plugin);
        File.WriteAllText(dll, "tampered");

        var act = () => Service().EnablePluginAsync(plugin.Id);

        await act.Should().ThrowAsync<PluginNotAllowedException>();
    }

    [Fact]
    public async Task ReloadingAPluginWhoseFilesChanged_IsRefused_AndNeverLoaded()
    {
        var dll = MakePlugin("p");
        var plugin = Row(dll);
        _db.Plugins.Add(plugin); await _db.SaveChangesAsync();
        await Sut().AcceptCurrentFilesAsync(plugin);
        File.WriteAllText(dll, "tampered");

        var act = () => Service().ReloadPluginAsync(plugin.PluginId);

        await act.Should().ThrowAsync<PluginNotAllowedException>();
    }
}
