using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class MediaItemTypeParentStubIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_media_items_MediaTypeId",
                table: "media_items");

            migrationBuilder.CreateIndex(
                name: "IX_media_items_MediaTypeId_ParentId_IsStub",
                table: "media_items",
                columns: new[] { "MediaTypeId", "ParentId", "IsStub" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_media_items_MediaTypeId_ParentId_IsStub",
                table: "media_items");

            migrationBuilder.CreateIndex(
                name: "IX_media_items_MediaTypeId",
                table: "media_items",
                column: "MediaTypeId");
        }
    }
}
