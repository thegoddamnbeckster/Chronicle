using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPeopleSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BirthDate",
                table: "media_items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeathDate",
                table: "media_items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "person_media_item_id",
                table: "media_credits",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "person_headshots",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    person_media_item_id = table.Column<int>(type: "INTEGER", nullable: false),
                    url = table.Column<string>(type: "TEXT", nullable: false),
                    thumbnail_url = table.Column<string>(type: "TEXT", nullable: true),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    first_seen_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_person_headshots", x => x.id);
                    table.ForeignKey(
                        name: "FK_person_headshots_media_items_person_media_item_id",
                        column: x => x.person_media_item_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_media_credits_person_item",
                table: "media_credits",
                column: "person_media_item_id");

            migrationBuilder.CreateIndex(
                name: "idx_person_headshots_person",
                table: "person_headshots",
                column: "person_media_item_id");

            migrationBuilder.CreateIndex(
                name: "idx_person_headshots_unique",
                table: "person_headshots",
                columns: new[] { "person_media_item_id", "url" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_media_credits_media_items_person_media_item_id",
                table: "media_credits",
                column: "person_media_item_id",
                principalTable: "media_items",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_media_credits_media_items_person_media_item_id",
                table: "media_credits");

            migrationBuilder.DropTable(
                name: "person_headshots");

            migrationBuilder.DropIndex(
                name: "idx_media_credits_person_item",
                table: "media_credits");

            migrationBuilder.DropColumn(
                name: "BirthDate",
                table: "media_items");

            migrationBuilder.DropColumn(
                name: "DeathDate",
                table: "media_items");

            migrationBuilder.DropColumn(
                name: "person_media_item_id",
                table: "media_credits");
        }
    }
}
