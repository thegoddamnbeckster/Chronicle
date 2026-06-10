using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoserMetadataJsonToMergeLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "loser_metadata_json",
                table: "media_item_merges",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "loser_metadata_json",
                table: "media_item_merges");
        }
    }
}
