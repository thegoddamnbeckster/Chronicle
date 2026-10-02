using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaItemLooseNameTypeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "idx_media_items_normalized_name_loose_mediatypeid",
                table: "media_items",
                columns: new[] { "normalized_name_loose", "MediaTypeId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_media_items_normalized_name_loose_mediatypeid",
                table: "media_items");
        }
    }
}
