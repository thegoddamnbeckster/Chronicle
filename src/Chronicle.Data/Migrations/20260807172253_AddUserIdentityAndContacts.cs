using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserIdentityAndContacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FirstName",
                table: "users",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Handle",
                table: "users",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastName",
                table: "users",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_contacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Value = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_contacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_contacts_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_Handle",
                table: "users",
                column: "Handle");

            migrationBuilder.CreateIndex(
                name: "IX_user_contacts_UserId_Kind",
                table: "user_contacts",
                columns: new[] { "UserId", "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_contacts");

            migrationBuilder.DropIndex(
                name: "IX_users_Handle",
                table: "users");

            migrationBuilder.DropColumn(
                name: "FirstName",
                table: "users");

            migrationBuilder.DropColumn(
                name: "Handle",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastName",
                table: "users");
        }
    }
}
