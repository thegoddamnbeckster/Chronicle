using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMergeAndDedupTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "normalized_name",
                table: "media_items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "media_item_aliases",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    media_item_id = table.Column<int>(type: "INTEGER", nullable: false),
                    alias = table.Column<string>(type: "TEXT", nullable: false),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_item_aliases", x => x.id);
                    table.ForeignKey(
                        name: "FK_media_item_aliases_media_items_media_item_id",
                        column: x => x.media_item_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_item_duplicate_candidates",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    item_a_id = table.Column<int>(type: "INTEGER", nullable: false),
                    item_b_id = table.Column<int>(type: "INTEGER", nullable: false),
                    detected_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_item_duplicate_candidates", x => x.id);
                    table.ForeignKey(
                        name: "FK_media_item_duplicate_candidates_media_items_item_a_id",
                        column: x => x.item_a_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_media_item_duplicate_candidates_media_items_item_b_id",
                        column: x => x.item_b_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_item_duplicate_dismissals",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    item_a_id = table.Column<int>(type: "INTEGER", nullable: false),
                    item_b_id = table.Column<int>(type: "INTEGER", nullable: false),
                    dismissed_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_item_duplicate_dismissals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "media_item_merges",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    winner_id = table.Column<int>(type: "INTEGER", nullable: false),
                    loser_original_id = table.Column<int>(type: "INTEGER", nullable: false),
                    loser_name = table.Column<string>(type: "TEXT", nullable: false),
                    loser_media_type_id = table.Column<int>(type: "INTEGER", nullable: false),
                    loser_hierarchy_level = table.Column<int>(type: "INTEGER", nullable: false),
                    loser_parent_id = table.Column<int>(type: "INTEGER", nullable: true),
                    loser_external_ids_json = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    loser_child_ids_json = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    merged_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    merged_by_user_id = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_item_merges", x => x.id);
                    table.ForeignKey(
                        name: "FK_media_item_merges_media_items_winner_id",
                        column: x => x.winner_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_media_items_normalized_name",
                table: "media_items",
                column: "normalized_name");

            migrationBuilder.CreateIndex(
                name: "idx_aliases_alias",
                table: "media_item_aliases",
                column: "alias");

            migrationBuilder.CreateIndex(
                name: "idx_aliases_media_item_id",
                table: "media_item_aliases",
                column: "media_item_id");

            migrationBuilder.CreateIndex(
                name: "idx_dup_candidates_unique",
                table: "media_item_duplicate_candidates",
                columns: new[] { "item_a_id", "item_b_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_item_duplicate_candidates_item_b_id",
                table: "media_item_duplicate_candidates",
                column: "item_b_id");

            migrationBuilder.CreateIndex(
                name: "idx_dup_dismissals_unique",
                table: "media_item_duplicate_dismissals",
                columns: new[] { "item_a_id", "item_b_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_merges_winner_id",
                table: "media_item_merges",
                column: "winner_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_item_aliases");

            migrationBuilder.DropTable(
                name: "media_item_duplicate_candidates");

            migrationBuilder.DropTable(
                name: "media_item_duplicate_dismissals");

            migrationBuilder.DropTable(
                name: "media_item_merges");

            migrationBuilder.DropIndex(
                name: "idx_media_items_normalized_name",
                table: "media_items");

            migrationBuilder.DropColumn(
                name: "normalized_name",
                table: "media_items");
        }
    }
}
