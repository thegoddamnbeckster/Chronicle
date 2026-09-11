using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKodiScanAcks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kodi_scan_acks",
                columns: table => new
                {
                    api_token_id = table.Column<int>(type: "INTEGER", nullable: false),
                    last_ack_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kodi_scan_acks", x => x.api_token_id);
                    table.ForeignKey(
                        name: "FK_kodi_scan_acks_api_tokens_api_token_id",
                        column: x => x.api_token_id,
                        principalTable: "api_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kodi_scan_acks");
        }
    }
}
