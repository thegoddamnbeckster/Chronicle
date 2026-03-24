-- Remove the wrong row (was pointing to deleted chronicle.plugin.tmdb\ folder)
DELETE FROM plugins WHERE Id = 5;

-- Add API key to the auto-registered row for plugins\tmdb\ (PluginId="chronicle.plugin.tmdb")
UPDATE plugins
SET SettingsJson = 'ENC:CfDJ8IDiGQKISnRGnqSOctlxNGvCJqV5XE3jFxVVTBmAkpAQwlkMBaDEdBYrC8D2-5rSP8IRmeDTVXda6X-EJ42Xwa-cKvnLzss-GzFQIweYec57OuK7x4xKLZC2tKhx_bwNRQOKmphq6tjzB0eRl09NqTRB0lTmJ_fUlrPcGpo2Rg4VVwy9EwRmVllIVlJnaNF1Fv9dnv3kWarsTfhFPMMMyGXbdohopQT_9nahaHFkp8x5ND5ycoJLepZ-GZx3nRLpHMBywHkMsbffjcuRd-hfrPAi0p_ys2IegjExPCc2Q8Q9'
WHERE Id = 7;

-- Rename enrichment rows from "tmdb" to "chronicle.plugin.tmdb" to match plugin PluginId
UPDATE media_item_enrichment_status
SET PluginId = 'chronicle.plugin.tmdb'
WHERE PluginId = 'tmdb';

-- Verify
SELECT Id, PluginId, Name, Version, IsEnabled, length(SettingsJson) as key_len, DllPath FROM plugins ORDER BY Id;
SELECT '---enrichment---';
SELECT PluginId, Status, COUNT(*) as cnt FROM media_item_enrichment_status GROUP BY PluginId, Status ORDER BY PluginId, Status;
