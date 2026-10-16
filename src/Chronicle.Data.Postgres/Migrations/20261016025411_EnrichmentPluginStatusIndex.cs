using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class EnrichmentPluginStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_media_enrichment_PluginId_Status_MediaItemId",
                table: "media_enrichment",
                columns: new[] { "PluginId", "Status", "MediaItemId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_media_enrichment_PluginId_Status_MediaItemId",
                table: "media_enrichment");
        }
    }
}
