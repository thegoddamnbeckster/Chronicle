using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class MediaItemSearchCoveringIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_media_items_HierarchyLevel_Name",
                table: "media_items");

            migrationBuilder.CreateIndex(
                name: "IX_media_items_HierarchyLevel_Name_normalized_name_MediaTypeId",
                table: "media_items",
                columns: new[] { "HierarchyLevel", "Name", "normalized_name", "MediaTypeId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_media_items_HierarchyLevel_Name_normalized_name_MediaTypeId",
                table: "media_items");

            migrationBuilder.CreateIndex(
                name: "IX_media_items_HierarchyLevel_Name",
                table: "media_items",
                columns: new[] { "HierarchyLevel", "Name" });
        }
    }
}
