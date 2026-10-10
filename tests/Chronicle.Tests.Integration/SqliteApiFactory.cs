using Chronicle.API;
using Chronicle.Services.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;

namespace Chronicle.Tests.Integration
{
    /// <summary>Records restart requests instead of actually stopping the test host.</summary>
    public sealed class RecordingRestart : IAppRestart
    {
        public List<string> Requests { get; } = [];
        public void Request(string reason) => Requests.Add(reason);
    }

    /// <summary>
    /// The full pipeline on a REAL SQLite file in a private scratch folder, with the real migrations applied
    /// at startup (the other factory uses the in-memory provider, which cannot back up or restore anything).
    /// The scratch folder is deleted when the factory is disposed.
    /// </summary>
    public sealed class SqliteApiFactory : WebApplicationFactory<Program>
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "chronicle-sqlite-api-tests-" + Guid.NewGuid().ToString("N"));
        public string DbFile => Path.Combine(Root, "chronicle.db");
        public string BackupDir => Path.Combine(Root, "backups");
        public RecordingRestart Restart { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(Root);
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={DbFile}");

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ICachedAppSettings>(TestAuthSettings.Permissive());
                services.AddSingleton<IAppRestart>(Restart);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, recursive: true); } catch { /* scratch */ }
        }
    }
}
