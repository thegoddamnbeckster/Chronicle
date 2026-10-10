using Chronicle.API;
using Chronicle.Services.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace Chronicle.Tests.Integration
{
    /// <summary>Runs only when a PostgreSQL server is offered through CHRONICLE_TEST_PG (a connection string to its "postgres" database).</summary>
    public sealed class PostgresFactAttribute : FactAttribute
    {
        public const string Variable = "CHRONICLE_TEST_PG";
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
                Skip = $"Set {Variable} to a PostgreSQL connection string (database=postgres) to run this.";
        }
    }

    /// <summary>
    /// The full pipeline on a throwaway PostgreSQL database created from the PostgreSQL migrations at startup (the same path
    /// a real PostgreSQL install takes). The database is dropped when the factory is disposed.
    /// </summary>
    public sealed class PostgresApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _admin = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable) ?? "";
        public string DatabaseName { get; } = "chronicle_it_" + Guid.NewGuid().ToString("N")[..10];

        public string ConnectionString =>
            new Npgsql.NpgsqlConnectionStringBuilder(_admin) { Database = DatabaseName }.ConnectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (string.IsNullOrWhiteSpace(_admin)) return;   // nothing runs without a server; the facts are skipped
            using (var admin = new Npgsql.NpgsqlConnection(_admin))
            {
                admin.Open();
                using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE \"{DatabaseName}\"";
                create.ExecuteNonQuery();
            }
            builder.UseEnvironment("Testing");
            builder.UseSetting("Database:Provider", "postgresql");
            builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString);
            builder.ConfigureServices(services => services.AddSingleton<ICachedAppSettings>(TestAuthSettings.Permissive()));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing || string.IsNullOrWhiteSpace(_admin)) return;
            Npgsql.NpgsqlConnection.ClearAllPools();
            try
            {
                using var admin = new Npgsql.NpgsqlConnection(_admin);
                admin.Open();
                using var drop = admin.CreateCommand();
                drop.CommandText = $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)";
                drop.ExecuteNonQuery();
            }
            catch { /* scratch */ }
        }
    }
}
