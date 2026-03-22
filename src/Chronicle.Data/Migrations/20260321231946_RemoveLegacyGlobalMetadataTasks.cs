using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyGlobalMetadataTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Remove the generic global task rows that were seeded by the old
            // MetadataEnrichmentScheduledTask and MetadataRefreshService IScheduledTask
            // implementations. These are replaced by per-plugin task rows seeded from
            // each plugin's manifest.json (chronicle.plugin.tmdb:fetch-missing-metadata, etc.)
            migrationBuilder.Sql("""
                DELETE FROM background_tasks
                WHERE TaskId IN ('metadata_enrichment', 'metadata_refresh');
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No restore — the old global tasks are superseded by per-plugin rows.
        }
    }
}
