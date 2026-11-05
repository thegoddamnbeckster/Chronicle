using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserGameOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_game_ownership",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Platform = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    PlaytimeMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    LastPlayedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AcquiredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OwnedViaSubscription = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsWishlist = table.Column<bool>(type: "INTEGER", nullable: false),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: true),
                    ImportStatusApplied = table.Column<string>(type: "TEXT", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_game_ownership", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_game_ownership_media_items_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_user_game_ownership_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_game_ownership_MediaItemId",
                table: "user_game_ownership",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_user_game_ownership_UserId_Source_SourceId",
                table: "user_game_ownership",
                columns: new[] { "UserId", "Source", "SourceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_game_ownership");
        }
    }
}
