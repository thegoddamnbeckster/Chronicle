using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class KodiDeviceAddonInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "addon_id",
                table: "kodi_devices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "addon_name",
                table: "kodi_devices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "addon_version",
                table: "kodi_devices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kodi_version",
                table: "kodi_devices",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "addon_id",
                table: "kodi_devices");

            migrationBuilder.DropColumn(
                name: "addon_name",
                table: "kodi_devices");

            migrationBuilder.DropColumn(
                name: "addon_version",
                table: "kodi_devices");

            migrationBuilder.DropColumn(
                name: "kodi_version",
                table: "kodi_devices");
        }
    }
}
