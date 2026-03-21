using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginBrandingAndTaskOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BrandColorDark",
                table: "plugins",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BrandColorLight",
                table: "plugins",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PluginId",
                table: "background_tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_plugins_PluginId",
                table: "plugins",
                column: "PluginId");

            migrationBuilder.CreateIndex(
                name: "IX_background_tasks_PluginId",
                table: "background_tasks",
                column: "PluginId");

            migrationBuilder.AddForeignKey(
                name: "FK_background_tasks_plugins_PluginId",
                table: "background_tasks",
                column: "PluginId",
                principalTable: "plugins",
                principalColumn: "PluginId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_background_tasks_plugins_PluginId",
                table: "background_tasks");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_plugins_PluginId",
                table: "plugins");

            migrationBuilder.DropIndex(
                name: "IX_background_tasks_PluginId",
                table: "background_tasks");

            migrationBuilder.DropColumn(
                name: "BrandColorDark",
                table: "plugins");

            migrationBuilder.DropColumn(
                name: "BrandColorLight",
                table: "plugins");

            migrationBuilder.DropColumn(
                name: "PluginId",
                table: "background_tasks");
        }
    }
}
