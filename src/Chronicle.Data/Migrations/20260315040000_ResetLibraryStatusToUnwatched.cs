using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class ResetLibraryStatusToUnwatched : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // All library entries were previously defaulted to 'Completed' on file-scanner
            // import regardless of actual watch status. Reset them to 'Unwatched' so the
            // user can set correct statuses once Simkl/Trakt/scrobbler plugins are available.
            // Entries that were manually changed to something other than Completed are left alone.
            migrationBuilder.Sql(
                "UPDATE user_libraries SET Status = 'Unwatched', CompletedAt = NULL WHERE Status = 'Completed';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE user_libraries SET Status = 'Completed' WHERE Status = 'Unwatched';");
        }
    }
}
