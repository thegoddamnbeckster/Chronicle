using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRelatedFilesAndScanHints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScanHintsJson",
                table: "media_types",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "media_item_related_files",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    DiscoveredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MissingSince = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_item_related_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_media_item_related_files_media_items_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 1,
                column: "ScanHintsJson",
                value: null);

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 2,
                column: "ScanHintsJson",
                value: null);

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 3,
                column: "ScanHintsJson",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_media_item_related_files_MediaItemId_Path",
                table: "media_item_related_files",
                columns: new[] { "MediaItemId", "Path" },
                unique: true);
        
            // Default scan hints for the built-in types (editable afterwards; an administrator's edit is never overwritten).
            migrationBuilder.Sql("UPDATE media_types SET ScanHintsJson = '{\"filePatterns\":[\"(?i)\\\\bS\\\\d{1,2}E\\\\d{1,3}\\\\b\",\"(?i)\\\\b\\\\d{1,2}x\\\\d{2,3}\\\\b\"],\"folderPatterns\":[\"(?i)^(season|series)\\\\s*\\\\d+$\",\"(?i)^specials$\"],\"extensions\":[]}' WHERE Name = 'tv' AND ScanHintsJson IS NULL;");
            migrationBuilder.Sql("UPDATE media_types SET ScanHintsJson = '{\"filePatterns\":[],\"folderPatterns\":[],\"extensions\":[\".mkv\",\".mp4\",\".avi\",\".m4v\",\".mov\",\".wmv\",\".ts\",\".m2ts\",\".mpg\",\".mpeg\"]}' WHERE Name = 'movies' AND ScanHintsJson IS NULL;");
            migrationBuilder.Sql("UPDATE media_types SET ScanHintsJson = '{\"filePatterns\":[],\"folderPatterns\":[],\"extensions\":[\".mp3\",\".flac\",\".m4a\",\".ogg\",\".opus\",\".wav\",\".wma\",\".aac\"]}' WHERE Name = 'music' AND ScanHintsJson IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_item_related_files");

            migrationBuilder.DropColumn(
                name: "ScanHintsJson",
                table: "media_types");
        }
    }
}
