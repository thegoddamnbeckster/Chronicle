using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class MediaItemTypeUpdatedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_media_items_MediaTypeId_UpdatedAt",
                table: "media_items",
                columns: new[] { "MediaTypeId", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_media_items_MediaTypeId_UpdatedAt",
                table: "media_items");
        }
    }
}
