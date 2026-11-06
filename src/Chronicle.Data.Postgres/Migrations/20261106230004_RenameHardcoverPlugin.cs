using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Postgres.Migrations
{
    /// <summary>
    /// PostgreSQL twin of the SQLite migration of the same name (see Chronicle.Data/Migrations/20261106230000_RenameHardcoverPlugin.cs
    /// for what moves and why): the Hardcover plugin id becomes "chronicle.plugin.hardcover" and every Hardcover book id
    /// gains its "hardcover:" prefix. Each statement only moves rows whose new form is not already present, so a repeated
    /// run is harmless. NOT yet run against a real PostgreSQL database.
    /// </summary>
    [DbContext(typeof(ChronicleDbContext))]
    [Migration("20261106230004_RenameHardcoverPlugin")]
    public partial class RenameHardcoverPlugin : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Book ids.
            migrationBuilder.Sql(@"
DELETE FROM media_external_ids
 WHERE ""Source"" = 'hardcover' AND ""ExternalId"" ~ '^[0-9]+$'
   AND EXISTS (SELECT 1 FROM media_external_ids o
                WHERE o.""MediaItemId"" = media_external_ids.""MediaItemId"" AND o.""Source"" = 'hardcover'
                  AND o.""ExternalId"" = 'hardcover:' || media_external_ids.""ExternalId"");");
            migrationBuilder.Sql(@"
UPDATE media_external_ids SET ""ExternalId"" = 'hardcover:' || ""ExternalId""
 WHERE ""Source"" = 'hardcover' AND ""ExternalId"" ~ '^[0-9]+$';");
            migrationBuilder.Sql(@"
UPDATE media_enrichment SET ""ExternalId"" = 'hardcover:' || ""ExternalId""
 WHERE ""PluginId"" IN ('hardcover', 'chronicle.plugin.hardcover') AND ""ExternalId"" ~ '^[0-9]+$';");

            // 2. The plugin row (settings and enabled flag kept).
            migrationBuilder.Sql(@"
INSERT INTO plugins (""Author"", ""BrandColorDark"", ""BrandColorLight"", ""Description"", ""DllPath"", ""InstalledAt"", ""IsEnabled"", ""Name"", ""PluginId"",
                     ""SettingsJson"", ""UpdatedAt"", ""Version"", ""IconUrl"", ""FixMatchHint"", ""LatestVersionAvailable"", ""UpdateCheckedAt"",
                     ""FilesChangedAt"", ""FilesSha256"", ""IntegrityBlockedAt"", ""PreviousFilesSha256"")
SELECT ""Author"", ""BrandColorDark"", ""BrandColorLight"", ""Description"", ""DllPath"", ""InstalledAt"", ""IsEnabled"", ""Name"", 'chronicle.plugin.hardcover',
       ""SettingsJson"", ""UpdatedAt"", ""Version"", ""IconUrl"", ""FixMatchHint"", ""LatestVersionAvailable"", ""UpdateCheckedAt"",
       ""FilesChangedAt"", ""FilesSha256"", ""IntegrityBlockedAt"", ""PreviousFilesSha256""
  FROM plugins o
 WHERE o.""PluginId"" = 'hardcover'
   AND NOT EXISTS (SELECT 1 FROM plugins n WHERE n.""PluginId"" = 'chronicle.plugin.hardcover');");
            migrationBuilder.Sql(@"
UPDATE plugins
   SET ""SettingsJson"" = (SELECT o.""SettingsJson"" FROM plugins o WHERE o.""PluginId"" = 'hardcover'),
       ""IsEnabled""    = (SELECT o.""IsEnabled""    FROM plugins o WHERE o.""PluginId"" = 'hardcover')
 WHERE ""PluginId"" = 'chronicle.plugin.hardcover'
   AND EXISTS (SELECT 1 FROM plugins o WHERE o.""PluginId"" = 'hardcover')
   AND COALESCE(""SettingsJson"", '') IN ('', '{}');");

            // 3. Background tasks.
            migrationBuilder.Sql(@"
UPDATE background_tasks
   SET ""TaskId"" = 'chronicle.plugin.hardcover:' || substr(""TaskId"", 11), ""PluginId"" = 'chronicle.plugin.hardcover'
 WHERE ""PluginId"" = 'hardcover'
   AND NOT EXISTS (SELECT 1 FROM background_tasks n
                    WHERE n.""TaskId"" = 'chronicle.plugin.hardcover:' || substr(background_tasks.""TaskId"", 11));");
            migrationBuilder.Sql(@"DELETE FROM background_tasks WHERE ""PluginId"" = 'hardcover';");

            // 4. Enrichment rows.
            migrationBuilder.Sql(@"
DELETE FROM media_enrichment
 WHERE ""PluginId"" = 'hardcover'
   AND EXISTS (SELECT 1 FROM media_enrichment n
                WHERE n.""MediaItemId"" = media_enrichment.""MediaItemId"" AND n.""PluginId"" = 'chronicle.plugin.hardcover');");
            migrationBuilder.Sql(@"UPDATE media_enrichment SET ""PluginId"" = 'chronicle.plugin.hardcover' WHERE ""PluginId"" = 'hardcover';");

            migrationBuilder.Sql(@"DELETE FROM plugins WHERE ""PluginId"" = 'hardcover';");

            // 5. The per-plugin blob key inside each item's metadata (a row that cannot be read as JSON is left alone).
            RenameBlobKey(migrationBuilder, "media_items", "Id", "MetadataJson");
            RenameBlobKey(migrationBuilder, "media_item_merges", "id", "loser_metadata_json");

            // 6. Settings that name the plugin.
            migrationBuilder.Sql(@"
UPDATE app_settings SET ""Value"" = REPLACE(""Value"", '""hardcover""', '""chronicle.plugin.hardcover""')
 WHERE ""Key"" IN ('metadata_assignment.config', 'plugin_display_order.config') AND ""Value"" LIKE '%""hardcover""%';");
            migrationBuilder.Sql(@"
UPDATE app_settings SET ""Value"" = REPLACE(""Value"", '""hardcover:', '""chronicle.plugin.hardcover:')
 WHERE ""Key"" = 'scheduler.task_priority_order' AND ""Value"" LIKE '%""hardcover:%';");
            migrationBuilder.Sql(@"
UPDATE app_settings SET ""Key"" = 'sync_state.chronicle.plugin.hardcover.last_synced_at'
 WHERE ""Key"" = 'sync_state.hardcover.last_synced_at'
   AND NOT EXISTS (SELECT 1 FROM app_settings n WHERE n.""Key"" = 'sync_state.chronicle.plugin.hardcover.last_synced_at');");
            migrationBuilder.Sql(@"
UPDATE app_settings SET ""Value"" = REPLACE(""Value"", '""plugin_id"": ""hardcover""', '""plugin_id"": ""chronicle.plugin.hardcover""')
 WHERE ""Key"" = 'plugins.catalog_cache' AND ""Value"" LIKE '%""plugin_id"": ""hardcover""%';");

            // 7. Open/closed state of the per-plugin sections.
            migrationBuilder.Sql(@"
UPDATE users SET preferences_json = REPLACE(preferences_json, '.hardcover""', '.chronicle.plugin.hardcover""')
 WHERE preferences_json LIKE '%.hardcover""%' AND preferences_json NOT LIKE '%chronicle.plugin.hardcover%';");
        }

        private static void RenameBlobKey(MigrationBuilder mb, string table, string idColumn, string column)
        {
            mb.Sql($@"
DO $$
DECLARE r record;
BEGIN
  FOR r IN SELECT ""{idColumn}"" AS id, ""{column}"" AS j FROM {table} WHERE ""{column}"" LIKE '%""hardcover"":%' LOOP
    BEGIN
      IF jsonb_typeof(r.j::jsonb -> 'hardcover') = 'object' THEN
        UPDATE {table}
           SET ""{column}"" = (CASE WHEN jsonb_exists(r.j::jsonb, 'chronicle.plugin.hardcover')
                                   THEN r.j::jsonb - 'hardcover'
                                   ELSE jsonb_set(r.j::jsonb - 'hardcover', '{{chronicle.plugin.hardcover}}', r.j::jsonb -> 'hardcover') END)::text
         WHERE ""{idColumn}"" = r.id;
      END IF;
    EXCEPTION WHEN others THEN
      NULL;
    END;
  END LOOP;
END $$;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: after the rename, new rows and blobs are written under the new id and the bare ids are gone.
        }
    }
}
