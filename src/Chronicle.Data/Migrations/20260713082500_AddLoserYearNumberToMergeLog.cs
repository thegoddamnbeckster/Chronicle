using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoserYearNumberToMergeLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "loser_year",
                table: "media_item_merges",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "loser_number",
                table: "media_item_merges",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "loser_year",
                table: "media_item_merges");

            migrationBuilder.DropColumn(
                name: "loser_number",
                table: "media_item_merges");
        }
    }
}
