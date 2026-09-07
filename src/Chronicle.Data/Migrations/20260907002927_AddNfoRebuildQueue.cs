using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNfoRebuildQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nfo_rebuild_queue",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    media_item_id = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    enqueued_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    claimed_by_kodi_device_id = table.Column<int>(type: "INTEGER", nullable: true),
                    claimed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    lease_expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    completed_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nfo_rebuild_queue", x => x.id);
                    table.ForeignKey(
                        name: "FK_nfo_rebuild_queue_media_items_media_item_id",
                        column: x => x.media_item_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_nfo_rebuild_queue_claimable",
                table: "nfo_rebuild_queue",
                columns: new[] { "completed_at", "claimed_by_kodi_device_id", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "idx_nfo_rebuild_queue_media_item",
                table: "nfo_rebuild_queue",
                column: "media_item_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nfo_rebuild_queue");
        }
    }
}
