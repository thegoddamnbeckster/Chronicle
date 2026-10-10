using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoviesProviderFamily : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Movie-like and show-like types are now read from the provider family, not from a list of names in code, so the
            // built-in types have to say so. Only a type that does not say yet is filled in; an administrator's edit is kept.
            migrationBuilder.Sql("UPDATE media_types SET \"ProviderFamily\" = 'movie' WHERE \"ProviderFamily\" IS NULL AND \"Id\" = 2 AND LOWER(\"Name\") = 'movies';");
            migrationBuilder.Sql("UPDATE media_types SET \"ProviderFamily\" = 'tv' WHERE \"ProviderFamily\" IS NULL AND \"Id\" = 1 AND LOWER(\"Name\") = 'tv';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 1,
                column: "ProviderFamily",
                value: null);

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 2,
                column: "ProviderFamily",
                value: null);

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 3,
                column: "ProviderFamily",
                value: null);
        }
    }
}
