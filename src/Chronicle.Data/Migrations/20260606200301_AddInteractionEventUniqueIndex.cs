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
            // Remove duplicate rows before creating the unique index.
            // For each (UserId, MediaItemId, Timestamp) group, keep the row with
            // the lowest Id and delete the rest. This handles any duplicates that
            // existed in the database before the idempotency check was added to
            // ScrobbleService.
            migrationBuilder.Sql(@"
                DELETE FROM interaction_events
                WHERE Id NOT IN (
                    SELECT MIN(Id)
                    FROM interaction_events
                    GROUP BY UserId, MediaItemId, Timestamp
                );
            ");

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
