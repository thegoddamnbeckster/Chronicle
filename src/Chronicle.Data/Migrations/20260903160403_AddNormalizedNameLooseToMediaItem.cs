using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNormalizedNameLooseToMediaItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "normalized_name_loose",
                table: "media_items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_media_items_normalized_name_loose",
                table: "media_items",
                column: "normalized_name_loose");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_media_items_normalized_name_loose",
                table: "media_items");

            migrationBuilder.DropColumn(
                name: "normalized_name_loose",
                table: "media_items");
        }
    }
}
