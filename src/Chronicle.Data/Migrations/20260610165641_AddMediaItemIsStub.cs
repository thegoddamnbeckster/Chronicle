using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaItemIsStub : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsStub",
                table: "media_items",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsStub",
                table: "media_items");
        }
    }
}
