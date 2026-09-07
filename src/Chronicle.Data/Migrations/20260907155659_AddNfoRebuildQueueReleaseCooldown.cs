using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNfoRebuildQueueReleaseCooldown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "last_released_at",
                table: "nfo_rebuild_queue",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "last_released_by_kodi_device_id",
                table: "nfo_rebuild_queue",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_nfo_rebuild_queue_last_released",
                table: "nfo_rebuild_queue",
                columns: new[] { "last_released_by_kodi_device_id", "last_released_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_nfo_rebuild_queue_last_released",
                table: "nfo_rebuild_queue");

            migrationBuilder.DropColumn(
                name: "last_released_at",
                table: "nfo_rebuild_queue");

            migrationBuilder.DropColumn(
                name: "last_released_by_kodi_device_id",
                table: "nfo_rebuild_queue");
        }
    }
}
