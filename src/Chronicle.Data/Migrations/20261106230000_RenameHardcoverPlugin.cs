using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <summary>
    /// The Hardcover plugin shipped with the plugin id "hardcover"; every other plugin is "chronicle.plugin.&lt;name&gt;".
    /// It is renamed to "chronicle.plugin.hardcover" (owner's decision, 2026-11-06), and every Hardcover book id is
    /// written with its source prefix ("hardcover:12345"), as Steam's are ("steam:...").
    ///
    /// A plugin id is stored in many places, so this moves all of them in one step, before the plugin host registers the
    /// plugin under its new id (a missed place would silently lose the owner's token, schedules, priorities or metadata):
    ///   * plugins (the row, with its settings)           * background_tasks (id "{plugin}:{task}" and the plugin)
    ///   * media_enrichment (the plugin)                   * media_items / media_item_merges metadata (the per-plugin blob key)
    ///   * app_settings: metadata assignment, plugin display order, task priority order, sync state, catalogue cache
    ///   * users.preferences_json (open/closed state of the per-plugin sections)
    /// Rows are only moved when the new form is not already there, so a run that was interrupted can be repeated.
    /// The short source name "hardcover" (external ids, credits, aliases) is unchanged: the host derives it from the id.
    /// </summary>
    [DbContext(typeof(ChronicleDbContext))]
    [Migration("20261106230000_RenameHardcoverPlugin")]
    public partial class RenameHardcoverPlugin : Migration
    {
        private const string Old = "hardcover";
        private const string New = "chronicle.plugin.hardcover";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Book ids: a bare number gains its prefix (a duplicate of one already prefixed on the same item is dropped).
            migrationBuilder.Sql(@"
DELETE FROM media_external_ids
 WHERE Source = 'hardcover' AND ExternalId <> '' AND ExternalId NOT GLOB '*[^0-9]*'
   AND EXISTS (SELECT 1 FROM media_external_ids o
                WHERE o.MediaItemId = media_external_ids.MediaItemId AND o.Source = 'hardcover'
                  AND o.ExternalId = 'hardcover:' || media_external_ids.ExternalId);");
            migrationBuilder.Sql(@"
UPDATE media_external_ids SET ExternalId = 'hardcover:' || ExternalId
 WHERE Source = 'hardcover' AND ExternalId <> '' AND ExternalId NOT GLOB '*[^0-9]*';");
            migrationBuilder.Sql(@"
UPDATE media_enrichment SET ExternalId = 'hardcover:' || ExternalId
 WHERE PluginId IN ('hardcover', 'chronicle.plugin.hardcover') AND ExternalId IS NOT NULL AND ExternalId <> ''
   AND ExternalId NOT GLOB '*[^0-9]*';");

            // 2. The plugin row: a copy under the new id (settings and enabled flag kept), then the old row goes.
            migrationBuilder.Sql(@"
INSERT INTO plugins (Author, BrandColorDark, BrandColorLight, Description, DllPath, InstalledAt, IsEnabled, Name, PluginId,
                     SettingsJson, UpdatedAt, Version, IconUrl, FixMatchHint, LatestVersionAvailable, UpdateCheckedAt,
                     FilesChangedAt, FilesSha256, IntegrityBlockedAt, PreviousFilesSha256)
SELECT Author, BrandColorDark, BrandColorLight, Description, DllPath, InstalledAt, IsEnabled, Name, 'chronicle.plugin.hardcover',
       SettingsJson, UpdatedAt, Version, IconUrl, FixMatchHint, LatestVersionAvailable, UpdateCheckedAt,
       FilesChangedAt, FilesSha256, IntegrityBlockedAt, PreviousFilesSha256
  FROM plugins o
 WHERE o.PluginId = 'hardcover'
   AND NOT EXISTS (SELECT 1 FROM plugins n WHERE n.PluginId = 'chronicle.plugin.hardcover');");
            // The host may already have registered the new id with empty settings: the owner's settings win.
            migrationBuilder.Sql(@"
UPDATE plugins
   SET SettingsJson = (SELECT o.SettingsJson FROM plugins o WHERE o.PluginId = 'hardcover'),
       IsEnabled    = (SELECT o.IsEnabled    FROM plugins o WHERE o.PluginId = 'hardcover')
 WHERE PluginId = 'chronicle.plugin.hardcover'
   AND EXISTS (SELECT 1 FROM plugins o WHERE o.PluginId = 'hardcover')
   AND COALESCE(SettingsJson, '') IN ('', '{}');");

            // 3. Its background tasks (schedule, enabled flag and last run kept), then anything left on the old id.
            migrationBuilder.Sql(@"
UPDATE background_tasks
   SET TaskId = 'chronicle.plugin.hardcover:' || substr(TaskId, 11), PluginId = 'chronicle.plugin.hardcover'
 WHERE PluginId = 'hardcover'
   AND NOT EXISTS (SELECT 1 FROM background_tasks n
                    WHERE n.TaskId = 'chronicle.plugin.hardcover:' || substr(background_tasks.TaskId, 11));");
            migrationBuilder.Sql("DELETE FROM background_tasks WHERE PluginId = 'hardcover';");

            // 4. Enrichment rows (one per item and plugin): drop an old row where the new id already has one, then move the rest.
            migrationBuilder.Sql(@"
DELETE FROM media_enrichment
 WHERE PluginId = 'hardcover'
   AND EXISTS (SELECT 1 FROM media_enrichment n
                WHERE n.MediaItemId = media_enrichment.MediaItemId AND n.PluginId = 'chronicle.plugin.hardcover');");
            migrationBuilder.Sql("UPDATE media_enrichment SET PluginId = 'chronicle.plugin.hardcover' WHERE PluginId = 'hardcover';");

            migrationBuilder.Sql("DELETE FROM plugins WHERE PluginId = 'hardcover';");

            // 5. The per-plugin metadata blob inside each item's metadata (an item that has both keeps the newer one).
            RenameBlobKey(migrationBuilder, "media_items", "MetadataJson");
            RenameBlobKey(migrationBuilder, "media_item_merges", "loser_metadata_json");

            // 6. Settings that name the plugin.
            migrationBuilder.Sql(@"
UPDATE app_settings SET Value = REPLACE(Value, '""hardcover""', '""chronicle.plugin.hardcover""')
 WHERE Key IN ('metadata_assignment.config', 'plugin_display_order.config') AND Value LIKE '%""hardcover""%';");
            migrationBuilder.Sql(@"
UPDATE app_settings SET Value = REPLACE(Value, '""hardcover:', '""chronicle.plugin.hardcover:')
 WHERE Key = 'scheduler.task_priority_order' AND Value LIKE '%""hardcover:%';");
            migrationBuilder.Sql(@"
UPDATE app_settings SET Key = 'sync_state.chronicle.plugin.hardcover.last_synced_at'
 WHERE Key = 'sync_state.hardcover.last_synced_at'
   AND NOT EXISTS (SELECT 1 FROM app_settings n WHERE n.Key = 'sync_state.chronicle.plugin.hardcover.last_synced_at');");
            migrationBuilder.Sql(@"
UPDATE app_settings SET Value = REPLACE(Value, '""plugin_id"": ""hardcover""', '""plugin_id"": ""chronicle.plugin.hardcover""')
 WHERE Key = 'plugins.catalog_cache' AND Value LIKE '%""plugin_id"": ""hardcover""%';");

            // 7. Open/closed state of the per-plugin sections ("media.{id}.hardcover", "backgroundTasks.hardcover").
            migrationBuilder.Sql(@"
UPDATE users SET preferences_json = REPLACE(preferences_json, '.hardcover""', '.chronicle.plugin.hardcover""')
 WHERE preferences_json LIKE '%.hardcover""%' AND preferences_json NOT LIKE '%chronicle.plugin.hardcover%';");
        }

        private static void RenameBlobKey(MigrationBuilder mb, string table, string column)
        {
            mb.Sql($@"
UPDATE {table}
   SET {column} = json_remove({column}, '$.hardcover')
 WHERE {column} LIKE '%""hardcover"":%' AND json_valid({column})
   AND json_type({column}, '$.hardcover') IS NOT NULL
   AND json_type({column}, '$.""chronicle.plugin.hardcover""') IS NOT NULL;");
            mb.Sql($@"
UPDATE {table}
   SET {column} = json_remove(json_set({column}, '$.""chronicle.plugin.hardcover""', json(json_extract({column}, '$.hardcover'))), '$.hardcover')
 WHERE {column} LIKE '%""hardcover"":%' AND json_valid({column})
   AND json_type({column}, '$.hardcover') = 'object';");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: after the rename, new rows and blobs are written under the new id and the bare ids are gone.
        }
    }
}
