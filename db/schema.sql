-- Chronicle database schema
-- Generated from EF Core migrations. Run this to create a fresh database.
-- SQLite only. To apply: sqlite3 chronicle-dev.db < db/schema.sql

PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

-- ── EF Migrations tracking ───────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "__EFMigrationsLock" (
    "Id"        INTEGER NOT NULL CONSTRAINT "PK___EFMigrationsLock" PRIMARY KEY,
    "Timestamp" TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId"    TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

-- ── Core tables ──────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "users" (
    "Id"           INTEGER NOT NULL CONSTRAINT "PK_users" PRIMARY KEY AUTOINCREMENT,
    "Username"     TEXT    NOT NULL,
    "Email"        TEXT    NULL,
    "PasswordHash" TEXT    NOT NULL,
    "DisplayName"  TEXT    NULL,
    "CreatedAt"    TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    "UpdatedAt"    TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    "LastLoginAt"  TEXT    NULL,
    "IsActive"     INTEGER NOT NULL,
    "IsAdmin"      INTEGER NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_users_Username" ON "users" ("Username");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_users_Email"    ON "users" ("Email");

CREATE TABLE IF NOT EXISTS "media_types" (
    "Id"              INTEGER NOT NULL CONSTRAINT "PK_media_types" PRIMARY KEY AUTOINCREMENT,
    "Name"            TEXT    NOT NULL,
    "DisplayName"     TEXT    NOT NULL,
    "Description"     TEXT    NULL,
    "HierarchyLevels" INTEGER NOT NULL,
    "HierarchyLabels" TEXT    NULL,
    "InteractionVerb" TEXT    NOT NULL DEFAULT 'watched',
    "ProgressUnit"    TEXT    NOT NULL DEFAULT 'minutes',
    "IsBuiltIn"       INTEGER NOT NULL,
    "IsActive"        INTEGER NOT NULL,
    "CreatedAt"       TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP)
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_media_types_Name" ON "media_types" ("Name");

CREATE TABLE IF NOT EXISTS "media_items" (
    "Id"             INTEGER NOT NULL CONSTRAINT "PK_media_items" PRIMARY KEY AUTOINCREMENT,
    "MediaTypeId"    INTEGER NOT NULL,
    "ParentId"       INTEGER NULL,
    "Name"           TEXT    NOT NULL,
    "SortName"       TEXT    NULL,
    "Year"           INTEGER NULL,
    "Overview"       TEXT    NULL,
    "PosterUrl"      TEXT    NULL,
    "RuntimeMinutes" INTEGER NULL,
    "HierarchyLevel" INTEGER NOT NULL,
    "Number"         INTEGER NULL,
    "MetadataJson"   TEXT    NULL,
    "CreatedAt"      TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    "UpdatedAt"      TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    CONSTRAINT "FK_media_items_media_types_MediaTypeId" FOREIGN KEY ("MediaTypeId") REFERENCES "media_types" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_media_items_media_items_ParentId"    FOREIGN KEY ("ParentId")    REFERENCES "media_items" ("Id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "IX_media_items_MediaTypeId" ON "media_items" ("MediaTypeId");
CREATE INDEX IF NOT EXISTS "IX_media_items_ParentId"    ON "media_items" ("ParentId");
CREATE INDEX IF NOT EXISTS "IX_media_items_Name"        ON "media_items" ("Name");

CREATE TABLE IF NOT EXISTS "media_external_ids" (
    "Id"          INTEGER NOT NULL CONSTRAINT "PK_media_external_ids" PRIMARY KEY AUTOINCREMENT,
    "MediaItemId" INTEGER NOT NULL,
    "Source"      TEXT    NOT NULL,
    "ExternalId"  TEXT    NOT NULL,
    CONSTRAINT "FK_media_external_ids_media_items_MediaItemId" FOREIGN KEY ("MediaItemId") REFERENCES "media_items" ("Id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "IX_media_external_ids_MediaItemId_Source" ON "media_external_ids" ("MediaItemId", "Source");
CREATE INDEX IF NOT EXISTS "IX_media_external_ids_Source_ExternalId"  ON "media_external_ids" ("Source", "ExternalId");

CREATE TABLE IF NOT EXISTS "interaction_events" (
    "Id"              INTEGER NOT NULL CONSTRAINT "PK_interaction_events" PRIMARY KEY AUTOINCREMENT,
    "UserId"          INTEGER NOT NULL,
    "MediaItemId"     INTEGER NOT NULL,
    "Timestamp"       TEXT    NOT NULL,
    "ProgressPercent" REAL    NULL,
    "DeviceName"      TEXT    NULL,
    "MarkedAsWatched" INTEGER NOT NULL,
    "CreatedAt"       TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    CONSTRAINT "FK_interaction_events_users_UserId"           FOREIGN KEY ("UserId")      REFERENCES "users" ("Id")       ON DELETE CASCADE,
    CONSTRAINT "FK_interaction_events_media_items_MediaItemId" FOREIGN KEY ("MediaItemId") REFERENCES "media_items" ("Id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "IX_interaction_events_UserId"           ON "interaction_events" ("UserId");
CREATE INDEX IF NOT EXISTS "IX_interaction_events_MediaItemId"      ON "interaction_events" ("MediaItemId");
CREATE INDEX IF NOT EXISTS "IX_interaction_events_Timestamp"        ON "interaction_events" ("Timestamp");
CREATE INDEX IF NOT EXISTS "IX_interaction_events_UserId_Timestamp" ON "interaction_events" ("UserId", "Timestamp");

CREATE TABLE IF NOT EXISTS "user_libraries" (
    "Id"          INTEGER NOT NULL CONSTRAINT "PK_user_libraries" PRIMARY KEY AUTOINCREMENT,
    "UserId"      INTEGER NOT NULL,
    "MediaItemId" INTEGER NOT NULL,
    "Status"      TEXT    NOT NULL,
    "UserRating"  INTEGER NULL,
    "Notes"       TEXT    NULL,
    "AddedAt"     TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    "UpdatedAt"   TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    "StartedAt"   TEXT    NULL,
    "CompletedAt" TEXT    NULL,
    CONSTRAINT "FK_user_libraries_users_UserId"           FOREIGN KEY ("UserId")      REFERENCES "users" ("Id")       ON DELETE CASCADE,
    CONSTRAINT "FK_user_libraries_media_items_MediaItemId" FOREIGN KEY ("MediaItemId") REFERENCES "media_items" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_user_libraries_UserId_MediaItemId" ON "user_libraries" ("UserId", "MediaItemId");
CREATE INDEX        IF NOT EXISTS "IX_user_libraries_MediaItemId"         ON "user_libraries" ("MediaItemId");
CREATE INDEX        IF NOT EXISTS "IX_user_libraries_Status"              ON "user_libraries" ("Status");

CREATE TABLE IF NOT EXISTS "api_tokens" (
    "Id"         INTEGER NOT NULL CONSTRAINT "PK_api_tokens" PRIMARY KEY AUTOINCREMENT,
    "UserId"     INTEGER NOT NULL,
    "Name"       TEXT    NOT NULL,
    "Token"      TEXT    NOT NULL,
    "CreatedAt"  TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    "LastUsedAt" TEXT    NULL,
    "ExpiresAt"  TEXT    NULL,
    "IsActive"   INTEGER NOT NULL,
    CONSTRAINT "FK_api_tokens_users_UserId" FOREIGN KEY ("UserId") REFERENCES "users" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_api_tokens_Token"  ON "api_tokens" ("Token");
CREATE INDEX        IF NOT EXISTS "IX_api_tokens_UserId" ON "api_tokens" ("UserId");

CREATE TABLE IF NOT EXISTS "plugins" (
    "Id"          INTEGER NOT NULL CONSTRAINT "PK_plugins" PRIMARY KEY AUTOINCREMENT,
    "PluginId"    TEXT    NOT NULL,
    "Name"        TEXT    NOT NULL,
    "Version"     TEXT    NOT NULL,
    "Author"      TEXT    NOT NULL,
    "Description" TEXT    NULL,
    "DllPath"     TEXT    NOT NULL,
    "IsEnabled"   INTEGER NOT NULL,
    "SettingsJson" TEXT   NULL,
    "InstalledAt" TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    "UpdatedAt"   TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP)
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_plugins_PluginId" ON "plugins" ("PluginId");

CREATE TABLE IF NOT EXISTS "media_lists" (
    "Id"          INTEGER NOT NULL CONSTRAINT "PK_media_lists" PRIMARY KEY AUTOINCREMENT,
    "UserId"      INTEGER NOT NULL,
    "Name"        TEXT    NOT NULL,
    "Description" TEXT    NULL,
    "IsOrdered"   INTEGER NOT NULL,
    "CreatedAt"   TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    "UpdatedAt"   TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    CONSTRAINT "FK_media_lists_users_UserId" FOREIGN KEY ("UserId") REFERENCES "users" ("Id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "IX_media_lists_UserId" ON "media_lists" ("UserId");

CREATE TABLE IF NOT EXISTS "media_list_items" (
    "Id"          INTEGER NOT NULL CONSTRAINT "PK_media_list_items" PRIMARY KEY AUTOINCREMENT,
    "ListId"      INTEGER NOT NULL,
    "MediaItemId" INTEGER NOT NULL,
    "Position"    INTEGER NOT NULL,
    "Notes"       TEXT    NULL,
    "AddedAt"     TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    CONSTRAINT "FK_media_list_items_media_lists_ListId"       FOREIGN KEY ("ListId")      REFERENCES "media_lists" ("Id")  ON DELETE CASCADE,
    CONSTRAINT "FK_media_list_items_media_items_MediaItemId"  FOREIGN KEY ("MediaItemId") REFERENCES "media_items" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_media_list_items_ListId_MediaItemId" ON "media_list_items" ("ListId", "MediaItemId");
CREATE INDEX        IF NOT EXISTS "IX_media_list_items_ListId"              ON "media_list_items" ("ListId");
CREATE INDEX        IF NOT EXISTS "IX_media_list_items_MediaItemId"         ON "media_list_items" ("MediaItemId");

CREATE TABLE IF NOT EXISTS "device_auth_codes" (
    "Id"          INTEGER NOT NULL CONSTRAINT "PK_device_auth_codes" PRIMARY KEY AUTOINCREMENT,
    "Code"        TEXT    NOT NULL,
    "DisplayCode" TEXT    NOT NULL,
    "DeviceName"  TEXT    NULL,
    "Status"      TEXT    NOT NULL,
    "RawApiKey"   TEXT    NULL,
    "UserId"      INTEGER NULL,
    "ApiTokenId"  INTEGER NULL,
    "ExpiresAt"   TEXT    NOT NULL,
    "CreatedAt"   TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    "ApprovedAt"  TEXT    NULL,
    CONSTRAINT "FK_device_auth_codes_users_UserId"         FOREIGN KEY ("UserId")     REFERENCES "users" ("Id")      ON DELETE SET NULL,
    CONSTRAINT "FK_device_auth_codes_api_tokens_ApiTokenId" FOREIGN KEY ("ApiTokenId") REFERENCES "api_tokens" ("Id") ON DELETE SET NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_device_auth_codes_Code"       ON "device_auth_codes" ("Code");
CREATE INDEX        IF NOT EXISTS "IX_device_auth_codes_UserId"     ON "device_auth_codes" ("UserId");
CREATE INDEX        IF NOT EXISTS "IX_device_auth_codes_ApiTokenId" ON "device_auth_codes" ("ApiTokenId");
CREATE INDEX        IF NOT EXISTS "IX_device_auth_codes_Status"     ON "device_auth_codes" ("Status");
CREATE INDEX        IF NOT EXISTS "IX_device_auth_codes_ExpiresAt"  ON "device_auth_codes" ("ExpiresAt");

-- ── EF migration history rows (keeps EF in sync with manually-created DB) ───

INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES
    ('20260227145330_InitialCreate',              '9.0.2'),
    ('20260227203318_AddPluginsTable',            '9.0.2'),
    ('20260228055205_AddMediaLists',              '9.0.2'),
    ('20260228222503_AddDeviceAuth',              '9.0.2'),
    ('20260301040908_AddMoviesAndMusicMediaTypes', '9.0.2');

-- ── Seed: built-in media types ───────────────────────────────────────────────

INSERT OR IGNORE INTO "media_types"
    ("Id", "Name", "DisplayName", "Description", "HierarchyLevels", "HierarchyLabels", "InteractionVerb", "ProgressUnit", "IsBuiltIn", "IsActive", "CreatedAt")
VALUES
    (1, 'tv',     'TV Shows', 'Television series, seasons, and episodes', 3, 'Show,Season,Episode', 'watched',  'minutes', 1, 1, '2026-01-01 00:00:00'),
    (2, 'movies', 'Movies',   'Feature films and short films',             1, 'Movie',               'watched',  'minutes', 1, 1, '2026-01-01 00:00:00'),
    (3, 'music',  'Music',    'Artists, albums, and tracks',               3, 'Artist,Album,Track',  'listened', 'tracks',  1, 1, '2026-01-01 00:00:00');
