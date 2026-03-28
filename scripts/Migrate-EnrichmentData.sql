-- Migrate-EnrichmentData.sql
-- Copies data from the old two-table enrichment system into the unified media_enrichment table.
-- Run this BEFORE applying the DropLegacyEnrichmentTables EF migration.
-- Note: EF generates PascalCase column names for all tables in this schema.

-- Step 1: Migrate media_item_enrichment_status → media_enrichment
-- Prefers media_external_ids.ExternalId for the same item+plugin (more reliable)
-- over the ExternalId stored on the enrichment_status row.
INSERT OR IGNORE INTO media_enrichment (
    "MediaItemId", "PluginId", "ExternalId", "Status",
    "RetryCount", "MaxRetries", "LastAttemptedAt", "LastCompletedAt",
    "ErrorMessage", "DiagnosticsJson"
)
SELECT
    es."MediaItemId",
    es."PluginId",
    COALESCE(
        (SELECT mei."ExternalId"
         FROM media_external_ids mei
         WHERE mei."MediaItemId" = es."MediaItemId"
           AND LOWER(mei."Source") = LOWER(es."PluginId")
         LIMIT 1),
        es."ExternalId"
    ) AS "ExternalId",
    es."Status",
    es."RetryCount",
    es."MaxRetries",
    es."LastAttemptedAt",
    es."LastCompletedAt",
    es."ErrorMessage",
    es."DiagnosticsJson"
FROM media_item_enrichment_status es;

-- Step 2: Items that only have a media_external_ids entry (no status row)
-- get a Completed row so they won't be re-searched.
INSERT OR IGNORE INTO media_enrichment (
    "MediaItemId", "PluginId", "ExternalId", "Status", "RetryCount", "MaxRetries"
)
SELECT
    mei."MediaItemId",
    mei."Source",
    mei."ExternalId",
    'Completed',
    0,
    3
FROM media_external_ids mei
WHERE NOT EXISTS (
    SELECT 1 FROM media_enrichment me
    WHERE me."MediaItemId" = mei."MediaItemId"
      AND LOWER(me."PluginId") = LOWER(mei."Source")
)
  AND mei."ExternalId" != '__suppress__';   -- don't migrate suppress sentinels
