using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKodiDevicesAndLibraryIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kodi_devices",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<int>(type: "INTEGER", nullable: false),
                    api_token_id = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    host = table.Column<string>(type: "TEXT", nullable: false),
                    port = table.Column<int>(type: "INTEGER", nullable: false),
                    username = table.Column<string>(type: "TEXT", nullable: true),
                    password = table.Column<string>(type: "TEXT", nullable: true),
                    last_seen_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kodi_devices", x => x.id);
                    table.ForeignKey(
                        name: "FK_kodi_devices_api_tokens_api_token_id",
                        column: x => x.api_token_id,
                        principalTable: "api_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_kodi_devices_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "kodi_library_ids",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    kodi_device_id = table.Column<int>(type: "INTEGER", nullable: false),
                    media_item_id = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    kodi_id = table.Column<int>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kodi_library_ids", x => x.id);
                    table.ForeignKey(
                        name: "FK_kodi_library_ids_kodi_devices_kodi_device_id",
                        column: x => x.kodi_device_id,
                        principalTable: "kodi_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_kodi_library_ids_media_items_media_item_id",
                        column: x => x.media_item_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_kodi_devices_api_token",
                table: "kodi_devices",
                column: "api_token_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_kodi_devices_user_id",
                table: "kodi_devices",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "idx_kodi_library_ids_unique",
                table: "kodi_library_ids",
                columns: new[] { "kodi_device_id", "media_item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_kodi_library_ids_media_item_id",
                table: "kodi_library_ids",
                column: "media_item_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kodi_library_ids");

            migrationBuilder.DropTable(
                name: "kodi_devices");
        }
    }
}
