using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInteractionEventUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_interaction_events_UserId_MediaItemId_Timestamp",
                table: "interaction_events",
                columns: new[] { "UserId", "MediaItemId", "Timestamp" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_interaction_events_UserId_MediaItemId_Timestamp",
                table: "interaction_events");
        }
    }
}
