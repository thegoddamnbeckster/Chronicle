using System.Net;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Chronicle.Tests.Unit.Services;

public sealed class PluginCatalogSourceTests
{
    private const string GoodFile = """
        { "version": 1, "plugins": [
          { "plugin_id": "chronicle.plugin.tmdb", "github_repo": "someone/Chronicle.Plugin.TMDB", "tags": ["movies", "tv"] },
          { "plugin_id": "my.new.plugin", "github_repo": "someone/My.New.Plugin", "tags": [] } ] }
        """;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<Uri> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Urls.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static (PluginCatalogSource Source, Handler Handler, ChronicleDbContext Db) Build(Func<HttpRequestMessage, HttpResponseMessage> respond, TimeProvider? clock = null)
    {
        var store = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ChronicleDbContext>(o => o.UseInMemoryDatabase(store));
        var provider = services.BuildServiceProvider();
        var handler = new Handler(respond);
        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(store).Options);
        return (new PluginCatalogSource(provider.GetRequiredService<IServiceScopeFactory>(), http.Object, clock), handler, db);
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task ReadsTheHostedFile_AndIsNotTheBuiltInList()
    {
        var (source, handler, _) = Build(_ => Ok(GoodFile));

        var listing = await source.GetAsync();

        listing.UsingFallback.Should().BeFalse();
        listing.Seeds.Select(s => s.PluginId).Should().Equal("chronicle.plugin.tmdb", "my.new.plugin");
        listing.Seeds[0].Tags.Should().Equal("movies", "tv");
        listing.Source.Should().Be(PluginCatalogSource.DefaultUrl);
        handler.Urls.Single().ToString().Should().Be(PluginCatalogSource.DefaultUrl);
    }

    [Fact]
    public async Task TheAddressIsAnAdministratorSetting()
    {
        var (source, handler, db) = Build(_ => Ok(GoodFile));
        db.AppSettings.Add(new AppSetting { Key = PluginCatalogSource.UrlKey, Value = "https://example.org/my-catalog.json" });
        db.AppSettings.Add(new AppSetting { Key = PluginIntegrity.AllowUnlistedKey, Value = "true" });
        await db.SaveChangesAsync();

        var listing = await source.GetAsync();

        handler.Urls.Single().ToString().Should().Be("https://example.org/my-catalog.json");
        listing.Source.Should().Be("https://example.org/my-catalog.json");
    }

    [Theory]
    [InlineData("http://example.org/catalog.json")]
    [InlineData("file:///C:/catalog.json")]
    [InlineData("ftp://example.org/catalog.json")]
    [InlineData("not a url")]
    public async Task OnlyHttpsAddressesAreFetched(string url)
    {
        var (source, handler, db) = Build(_ => Ok(GoodFile));
        db.AppSettings.Add(new AppSetting { Key = PluginCatalogSource.UrlKey, Value = url });
        db.AppSettings.Add(new AppSetting { Key = PluginIntegrity.AllowUnlistedKey, Value = "true" });
        await db.SaveChangesAsync();

        var listing = await source.GetAsync();

        handler.Calls.Should().Be(0);
        listing.UsingFallback.Should().BeTrue();
        listing.Error.Should().Contain("https");
        listing.Seeds.Should().BeEquivalentTo(PluginCatalogSeeds.Entries);
    }

    [Fact]
    public async Task ACustomAddress_IsIgnored_UnlessUnlistedPluginsAreAllowed()
    {
        var (source, handler, db) = Build(_ => Ok(GoodFile));
        db.AppSettings.Add(new AppSetting { Key = PluginCatalogSource.UrlKey, Value = "https://attacker.example/plugins.json" });
        await db.SaveChangesAsync();

        var listing = await source.GetAsync();

        handler.Urls.Single().ToString().Should().Be(PluginCatalogSource.DefaultUrl);
        listing.Source.Should().Be(PluginCatalogSource.DefaultUrl);
    }

    [Fact]
    public async Task WhenTheFileCannotBeReached_TheLastGoodCopyIsUsed()
    {
        var up = true;
        var (source, _, _) = Build(_ => up ? Ok(GoodFile) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        (await source.GetAsync()).Seeds.Should().HaveCount(2);

        up = false;
        source.Refresh();
        var listing = await source.GetAsync();

        listing.UsingFallback.Should().BeTrue();
        listing.Source.Should().StartWith("last successful copy");
        listing.Seeds.Select(s => s.PluginId).Should().Contain("my.new.plugin");
    }

    [Fact]
    public async Task WithNeitherTheFileNorACopy_TheBuiltInListKeepsTheCatalogWorking()
    {
        var (source, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var listing = await source.GetAsync();

        listing.UsingFallback.Should().BeTrue();
        listing.Source.Should().Be("built-in");
        listing.Seeds.Should().NotBeEmpty();
        listing.Error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{ \"plugins\": [] }")]
    [InlineData("{ \"plugins\": [ { \"plugin_id\": \"x\" } ] }")]
    public async Task AFileThatIsNotACatalog_IsNeverTrusted_AndIsNotRemembered(string body)
    {
        var (source, _, db) = Build(_ => Ok(body));

        var listing = await source.GetAsync();

        listing.UsingFallback.Should().BeTrue();
        listing.Source.Should().Be("built-in");
        (await db.AppSettings.AnyAsync(s => s.Key == PluginCatalogSource.CacheKey)).Should().BeFalse();
    }

    [Fact]
    public void BadEntriesAreSkipped_TheRestKept()
    {
        var seeds = PluginCatalogSource.Parse("""
            { "plugins": [
              { "plugin_id": "good.one", "github_repo": "o/r" },
              { "plugin_id": "../evil", "github_repo": "o/r" },
              { "plugin_id": "bad.repo", "github_repo": "https://evil.example/x" },
              { "plugin_id": "bad.repo2", "github_repo": "no-slash" },
              { "plugin_id": "good.one", "github_repo": "o/other" },
              { "plugin_id": "good.two", "github_repo": "o/r2", "tags": ["a", " ", "b"] } ] }
            """, out var problem);

        problem.Should().BeNull();
        seeds!.Select(s => s.PluginId).Should().Equal("good.one", "good.two");
        seeds[0].GithubRepo.Should().Be("o/r");     // the first of two with the same id wins
        seeds[1].Tags.Should().Equal("a", "b");
    }

    [Fact]
    public async Task TheListIsCached_UntilRefreshedOrExpired()
    {
        var clock = new FakeClock();
        var (source, handler, _) = Build(_ => Ok(GoodFile), clock);

        await source.GetAsync();
        await source.GetAsync();
        handler.Calls.Should().Be(1);

        clock.Advance(TimeSpan.FromMinutes(16));
        await source.GetAsync();
        handler.Calls.Should().Be(2);

        source.Refresh();
        await source.GetAsync();
        handler.Calls.Should().Be(3);
    }

    [Fact]
    public async Task TheAllowlist_FollowsTheHostedCatalog()
    {
        var (source, _, db) = Build(_ => Ok(GoodFile));
        var env = new Mock<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());
        var integrity = new PluginIntegrity(db, env.Object, null, source);

        (await integrity.IsAllowedAsync("my.new.plugin")).Should().BeTrue();        // added to the hosted file only
        (await integrity.IsAllowedAsync("chronicle.plugin.trakt")).Should().BeFalse();   // in the built-in list, not in this catalog
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
