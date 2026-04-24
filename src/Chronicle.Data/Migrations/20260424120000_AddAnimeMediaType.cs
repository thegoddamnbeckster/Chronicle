using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    [DbContext(typeof(ChronicleDbContext))]
    [Migration("20260424120000_AddAnimeMediaType")]
    public partial class AddAnimeMediaType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "media_types",
                columns: new[] { "Id", "CreatedAt", "Description", "DisplayName",
                                 "HierarchyLabels", "HierarchyLevels", "InteractionVerb",
                                 "IsActive", "IsBuiltIn", "Name", "ProgressUnit" },
                values: new object[]
                {
                    5,
                    new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc),
                    "Anime series, seasons, and episodes",
                    "Anime",
                    "Show,Season,Episode",
                    3,
                    "watched",
                    true,
                    true,
                    "anime",
                    "episodes"
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 5);
        }
    }
}
