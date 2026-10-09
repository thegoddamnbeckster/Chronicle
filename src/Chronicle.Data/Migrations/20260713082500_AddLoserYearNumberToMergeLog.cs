using Chronicle.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    // These two were written without the [DbContext]/[Migration] attributes EF finds migrations by, so a brand-new
    // database never ran them and lacked the columns (merging items and deleting users then failed on it). Databases that
    // already have the columns also have the history row, so they skip this.
    [DbContext(typeof(ChronicleDbContext))]
    [Migration("20260713082500_AddLoserYearNumberToMergeLog")]
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
