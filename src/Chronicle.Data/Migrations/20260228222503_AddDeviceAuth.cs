using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_auth_codes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DisplayCode = table.Column<string>(type: "TEXT", maxLength: 9, nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    RawApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ApiTokenId = table.Column<int>(type: "INTEGER", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ApprovedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_auth_codes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_device_auth_codes_api_tokens_ApiTokenId",
                        column: x => x.ApiTokenId,
                        principalTable: "api_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_device_auth_codes_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_device_auth_codes_ApiTokenId",
                table: "device_auth_codes",
                column: "ApiTokenId");

            migrationBuilder.CreateIndex(
                name: "IX_device_auth_codes_Code",
                table: "device_auth_codes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_auth_codes_ExpiresAt",
                table: "device_auth_codes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_device_auth_codes_Status",
                table: "device_auth_codes",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_device_auth_codes_UserId",
                table: "device_auth_codes",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_auth_codes");
        }
    }
}
