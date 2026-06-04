using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDismissalForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_media_item_duplicate_dismissals_item_b_id",
                table: "media_item_duplicate_dismissals",
                column: "item_b_id");

            migrationBuilder.AddForeignKey(
                name: "FK_media_item_duplicate_dismissals_media_items_item_a_id",
                table: "media_item_duplicate_dismissals",
                column: "item_a_id",
                principalTable: "media_items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_media_item_duplicate_dismissals_media_items_item_b_id",
                table: "media_item_duplicate_dismissals",
                column: "item_b_id",
                principalTable: "media_items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_media_item_duplicate_dismissals_media_items_item_a_id",
                table: "media_item_duplicate_dismissals");

            migrationBuilder.DropForeignKey(
                name: "FK_media_item_duplicate_dismissals_media_items_item_b_id",
                table: "media_item_duplicate_dismissals");

            migrationBuilder.DropIndex(
                name: "IX_media_item_duplicate_dismissals_item_b_id",
                table: "media_item_duplicate_dismissals");
        }
    }
}
