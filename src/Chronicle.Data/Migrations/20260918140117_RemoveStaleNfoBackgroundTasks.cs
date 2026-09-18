using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveStaleNfoBackgroundTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DropNfoRebuildQueue (2026-09-14) dropped the nfo_rebuild_queue table when the
            // server-side NFO generation system was removed, but missed these two seeded
            // background_tasks rows -- NfoGenerationService/NfoPushService are gone, so
            // TaskSchedulerService.TickAsync logs "no IScheduledTask found for DB row" for them
            // every 30s forever, and the Settings > Background Tasks page shows two dead entries
            // with stale SUCCESS/FAILED status from their last run before removal.
            // SeedTasksAsync only ever adds missing rows, never removes stale ones, so this never
            // self-heals without a migration.
            migrationBuilder.Sql(
                "DELETE FROM background_tasks WHERE TaskId IN ('nfo_generation', 'nfo-push');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately not reversible -- NfoGenerationService/NfoPushService no longer exist,
            // so re-inserting these rows would just recreate the same dead entries.
        }
    }
}
