using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFanEditsMediaType : Migration
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
                    4,
                    new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc),
                    "Fan-edited versions of movies — reworked cuts, custom edits, colour grades",
                    "Fan Edits",
                    "Fan Edit",
                    1,
                    "watched",
                    true,
                    true,
                    "fanedits",
                    "minutes"
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 4);
        }
    }
}
