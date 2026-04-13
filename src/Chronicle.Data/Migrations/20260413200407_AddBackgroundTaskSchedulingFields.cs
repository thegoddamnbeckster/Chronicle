using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBackgroundTaskSchedulingFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RunConfirmationMessage",
                table: "background_tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RunConfirmationTitle",
                table: "background_tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Schedulable",
                table: "background_tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RunConfirmationMessage",
                table: "background_tasks");

            migrationBuilder.DropColumn(
                name: "RunConfirmationTitle",
                table: "background_tasks");

            migrationBuilder.DropColumn(
                name: "Schedulable",
                table: "background_tasks");
        }
    }
}
