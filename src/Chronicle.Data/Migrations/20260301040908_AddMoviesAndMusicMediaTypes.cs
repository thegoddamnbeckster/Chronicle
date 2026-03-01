using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMoviesAndMusicMediaTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "media_types",
                columns: new[] { "Id", "CreatedAt", "Description", "DisplayName", "HierarchyLabels", "HierarchyLevels", "InteractionVerb", "IsActive", "IsBuiltIn", "Name", "ProgressUnit" },
                values: new object[,]
                {
                    { 2, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Feature films and short films", "Movies", "Movie", 1, "watched", true, true, "movies", "minutes" },
                    { 3, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Artists, albums, and tracks", "Music", "Artist,Album,Track", 3, "listened", true, true, "music", "tracks" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 2);

            migrationBuilder.DeleteData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 3);
        }
    }
}
