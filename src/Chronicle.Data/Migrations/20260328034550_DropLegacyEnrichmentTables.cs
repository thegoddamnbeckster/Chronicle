using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyEnrichmentTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_item_enrichment_status");

            migrationBuilder.DropTable(
                name: "media_item_refresh_log");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "media_item_enrichment_status",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    DiagnosticsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: true),
                    LastAttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MaxRetries = table.Column<int>(type: "INTEGER", nullable: false),
                    PluginId = table.Column<string>(type: "TEXT", nullable: false),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_item_enrichment_status", x => x.Id);
                    table.ForeignKey(
                        name: "FK_media_item_enrichment_status_media_items_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_item_refresh_log",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ProviderName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RefreshedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_item_refresh_log", x => x.Id);
                    table.ForeignKey(
                        name: "FK_media_item_refresh_log_media_items_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_media_item_enrichment_status_MediaItemId_PluginId",
                table: "media_item_enrichment_status",
                columns: new[] { "MediaItemId", "PluginId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_item_refresh_log_MediaItemId_ProviderName",
                table: "media_item_refresh_log",
                columns: new[] { "MediaItemId", "ProviderName" });
        }
    }
}
