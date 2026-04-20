using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "media_credits",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    media_item_id = table.Column<int>(type: "INTEGER", nullable: false),
                    person_name = table.Column<string>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    character_name = table.Column<string>(type: "TEXT", nullable: true),
                    billing_order = table.Column<int>(type: "INTEGER", nullable: true),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    external_person_id = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_credits", x => x.id);
                    table.ForeignKey(
                        name: "FK_media_credits_media_items_media_item_id",
                        column: x => x.media_item_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_media_credits_item",
                table: "media_credits",
                column: "media_item_id");

            migrationBuilder.CreateIndex(
                name: "idx_media_credits_person",
                table: "media_credits",
                column: "person_name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_credits");
        }
    }
}
