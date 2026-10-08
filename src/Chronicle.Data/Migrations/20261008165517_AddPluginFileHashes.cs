using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginFileHashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FilesChangedAt",
                table: "plugins",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilesSha256",
                table: "plugins",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IntegrityBlockedAt",
                table: "plugins",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousFilesSha256",
                table: "plugins",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FilesChangedAt",
                table: "plugins");

            migrationBuilder.DropColumn(
                name: "FilesSha256",
                table: "plugins");

            migrationBuilder.DropColumn(
                name: "IntegrityBlockedAt",
                table: "plugins");

            migrationBuilder.DropColumn(
                name: "PreviousFilesSha256",
                table: "plugins");
        }
    }
}
