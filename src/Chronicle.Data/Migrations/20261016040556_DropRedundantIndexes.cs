using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropRedundantIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_person_headshots_person",
                table: "person_headshots");

            migrationBuilder.DropIndex(
                name: "IX_media_list_items_ListId",
                table: "media_list_items");

            migrationBuilder.DropIndex(
                name: "idx_media_items_normalized_name",
                table: "media_items");

            migrationBuilder.DropIndex(
                name: "idx_media_items_normalized_name_loose",
                table: "media_items");

            migrationBuilder.DropIndex(
                name: "IX_interaction_events_UserId",
                table: "interaction_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "idx_person_headshots_person",
                table: "person_headshots",
                column: "person_media_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_media_list_items_ListId",
                table: "media_list_items",
                column: "ListId");

            migrationBuilder.CreateIndex(
                name: "idx_media_items_normalized_name",
                table: "media_items",
                column: "normalized_name");

            migrationBuilder.CreateIndex(
                name: "idx_media_items_normalized_name_loose",
                table: "media_items",
                column: "normalized_name_loose");

            migrationBuilder.CreateIndex(
                name: "IX_interaction_events_UserId",
                table: "interaction_events",
                column: "UserId");
        }
    }
}
