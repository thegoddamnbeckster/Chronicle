using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Chronicle.Data.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The SQLite model gives media_item_known_file_names.FileName the NOCASE collation (file names are looked up
            // case-insensitively). PostgreSQL has no such collation; its equivalent is the citext type, hence this edit
            // to the generated migration. Re-apply it if this migration is ever regenerated.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS citext;");

            migrationBuilder.CreateTable(
                name: "app_settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "media_types",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    HierarchyLevels = table.Column<int>(type: "integer", nullable: false),
                    HierarchyLabels = table.Column<string>(type: "text", nullable: true),
                    InteractionVerb = table.Column<string>(type: "text", nullable: false, defaultValue: "watched"),
                    ProgressUnit = table.Column<string>(type: "text", nullable: false, defaultValue: "minutes"),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsCollections = table.Column<bool>(type: "boolean", nullable: false),
                    IsTrackable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    ScanStrategy = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    IsUserModified = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ScanHintsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ProviderFamily = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    CastHeading = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_types", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "plugins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PluginId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Author = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    DllPath = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SettingsJson = table.Column<string>(type: "text", nullable: true),
                    InstalledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    IconUrl = table.Column<string>(type: "text", nullable: true),
                    BrandColorLight = table.Column<string>(type: "text", nullable: true),
                    BrandColorDark = table.Column<string>(type: "text", nullable: true),
                    FixMatchHint = table.Column<string>(type: "text", nullable: true),
                    LatestVersionAvailable = table.Column<string>(type: "text", nullable: true),
                    UpdateCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FilesSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreviousFilesSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FilesChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IntegrityBlockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugins", x => x.Id);
                    table.UniqueConstraint("AK_plugins_PluginId", x => x.PluginId);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Username = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Email = table.Column<string>(type: "text", nullable: true),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Handle = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    LastLoginAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    preferences_json = table.Column<string>(type: "text", nullable: false, defaultValue: "{}")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "media_items",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MediaTypeId = table.Column<int>(type: "integer", nullable: false),
                    ParentId = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SortName = table.Column<string>(type: "text", nullable: true),
                    normalized_name = table.Column<string>(type: "text", nullable: true),
                    normalized_name_loose = table.Column<string>(type: "text", nullable: true),
                    Year = table.Column<int>(type: "integer", nullable: true),
                    Overview = table.Column<string>(type: "text", nullable: true),
                    PosterUrl = table.Column<string>(type: "text", nullable: true),
                    RuntimeMinutes = table.Column<int>(type: "integer", nullable: true),
                    BirthDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeathDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HierarchyLevel = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: true),
                    SeriesPosition = table.Column<double>(type: "double precision", nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    IsStub = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_media_items_media_items_ParentId",
                        column: x => x.ParentId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_media_items_media_types_MediaTypeId",
                        column: x => x.MediaTypeId,
                        principalTable: "media_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scan_folders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    MediaTypeId = table.Column<int>(type: "integer", nullable: true),
                    BundleRelatedFiles = table.Column<bool>(type: "boolean", nullable: true),
                    Recursive = table.Column<bool>(type: "boolean", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    LastScannedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scan_folders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scan_folders_media_types_MediaTypeId",
                        column: x => x.MediaTypeId,
                        principalTable: "media_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "background_tasks",
                columns: table => new
                {
                    TaskId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    CronExpression = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunSucceeded = table.Column<bool>(type: "boolean", nullable: true),
                    LastErrorMessage = table.Column<string>(type: "text", nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PluginId = table.Column<string>(type: "character varying(200)", nullable: true),
                    Schedulable = table.Column<bool>(type: "boolean", nullable: false),
                    RunConfirmationTitle = table.Column<string>(type: "text", nullable: true),
                    RunConfirmationMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_background_tasks", x => x.TaskId);
                    table.ForeignKey(
                        name: "FK_background_tasks_plugins_PluginId",
                        column: x => x.PluginId,
                        principalTable: "plugins",
                        principalColumn: "PluginId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "api_tokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Token = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "full")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_api_tokens_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_lists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    IsOrdered = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_lists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_media_lists_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Link = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    DedupeKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notifications_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "password_reset_tokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IssuedByUserId = table.Column<int>(type: "integer", nullable: true),
                    Delivery = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_reset_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_password_reset_tokens_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_contacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_contacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_contacts_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "interaction_events",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    MediaItemId = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProgressPercent = table.Column<double>(type: "double precision", nullable: true),
                    DeviceName = table.Column<string>(type: "text", nullable: true),
                    MarkedAsWatched = table.Column<bool>(type: "boolean", nullable: false),
                    IsApproximateTimestamp = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interaction_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_interaction_events_media_items_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_interaction_events_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_credits",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    media_item_id = table.Column<int>(type: "integer", nullable: false),
                    person_name = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    character_name = table.Column<string>(type: "text", nullable: true),
                    billing_order = table.Column<int>(type: "integer", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    external_person_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    person_media_item_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_credits", x => x.id);
                    table.ForeignKey(
                        name: "FK_media_credits_media_items_media_item_id",
                        column: x => x.media_item_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_media_credits_media_items_person_media_item_id",
                        column: x => x.person_media_item_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "media_enrichment",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MediaItemId = table.Column<int>(type: "integer", nullable: false),
                    PluginId = table.Column<string>(type: "text", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    MaxRetries = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    DiagnosticsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_enrichment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_media_enrichment_media_items_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_external_ids",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MediaItemId = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_external_ids", x => x.Id);
                    table.ForeignKey(
                        name: "FK_media_external_ids_media_items_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_item_aliases",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    media_item_id = table.Column<int>(type: "integer", nullable: false),
                    alias = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_item_aliases", x => x.id);
                    table.ForeignKey(
                        name: "FK_media_item_aliases_media_items_media_item_id",
                        column: x => x.media_item_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_item_duplicate_candidates",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    item_a_id = table.Column<int>(type: "integer", nullable: false),
                    item_b_id = table.Column<int>(type: "integer", nullable: false),
                    detected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_item_duplicate_candidates", x => x.id);
                    table.ForeignKey(
                        name: "FK_media_item_duplicate_candidates_media_items_item_a_id",
                        column: x => x.item_a_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_media_item_duplicate_candidates_media_items_item_b_id",
                        column: x => x.item_b_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_item_duplicate_dismissals",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    item_a_id = table.Column<int>(type: "integer", nullable: false),
                    item_b_id = table.Column<int>(type: "integer", nullable: false),
                    dismissed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_item_duplicate_dismissals", x => x.id);
                    table.ForeignKey(
                        name: "FK_media_item_duplicate_dismissals_media_items_item_a_id",
                        column: x => x.item_a_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_media_item_duplicate_dismissals_media_items_item_b_id",
                        column: x => x.item_b_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_item_known_file_names",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MediaItemId = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "citext", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_item_known_file_names", x => x.Id);
                    table.ForeignKey(
                        name: "FK_media_item_known_file_names_media_items_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_item_merges",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    winner_id = table.Column<int>(type: "integer", nullable: false),
                    loser_original_id = table.Column<int>(type: "integer", nullable: false),
                    loser_name = table.Column<string>(type: "text", nullable: false),
                    loser_media_type_id = table.Column<int>(type: "integer", nullable: false),
                    loser_hierarchy_level = table.Column<int>(type: "integer", nullable: false),
                    loser_parent_id = table.Column<int>(type: "integer", nullable: true),
                    loser_year = table.Column<int>(type: "integer", nullable: true),
                    loser_number = table.Column<int>(type: "integer", nullable: true),
                    loser_series_position = table.Column<double>(type: "double precision", nullable: true),
                    loser_external_ids_json = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    loser_child_ids_json = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    loser_metadata_json = table.Column<string>(type: "text", nullable: true),
                    merged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    merged_by_user_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_item_merges", x => x.id);
                    table.ForeignKey(
                        name: "FK_media_item_merges_media_items_winner_id",
                        column: x => x.winner_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_item_related_files",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MediaItemId = table.Column<int>(type: "integer", nullable: false),
                    Path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    DiscoveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MissingSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_item_related_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_media_item_related_files_media_items_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "person_headshots",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    person_media_item_id = table.Column<int>(type: "integer", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    thumbnail_url = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    first_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_person_headshots", x => x.id);
                    table.ForeignKey(
                        name: "FK_person_headshots_media_items_person_media_item_id",
                        column: x => x.person_media_item_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "person_provider_credits",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    person_media_item_id = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    media_type = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: true),
                    poster_url = table.Column<string>(type: "text", nullable: true),
                    role = table.Column<string>(type: "text", nullable: false),
                    character_name = table.Column<string>(type: "text", nullable: true),
                    fetched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_person_provider_credits", x => x.id);
                    table.ForeignKey(
                        name: "FK_person_provider_credits_media_items_person_media_item_id",
                        column: x => x.person_media_item_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_libraries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    MediaItemId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    UserRating = table.Column<int>(type: "integer", nullable: true),
                    UserRatingUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResumePositionPercent = table.Column<double>(type: "double precision", nullable: true),
                    ResumeUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastKnownProgressPercent = table.Column<double>(type: "double precision", nullable: true),
                    LastKnownProgressAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WatchResetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_libraries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_libraries_media_items_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_libraries_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_auth_codes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DisplayCode = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    DeviceName = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RawApiKey = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    ApiTokenId = table.Column<int>(type: "integer", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_auth_codes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_device_auth_codes_api_tokens_ApiTokenId",
                        column: x => x.ApiTokenId,
                        principalTable: "api_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_device_auth_codes_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "kodi_devices",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    api_token_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    username = table.Column<string>(type: "text", nullable: true),
                    password = table.Column<string>(type: "text", nullable: true),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kodi_devices", x => x.id);
                    table.ForeignKey(
                        name: "FK_kodi_devices_api_tokens_api_token_id",
                        column: x => x.api_token_id,
                        principalTable: "api_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_kodi_devices_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "kodi_scan_acks",
                columns: table => new
                {
                    api_token_id = table.Column<int>(type: "integer", nullable: false),
                    last_ack_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kodi_scan_acks", x => x.api_token_id);
                    table.ForeignKey(
                        name: "FK_kodi_scan_acks_api_tokens_api_token_id",
                        column: x => x.api_token_id,
                        principalTable: "api_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_list_items",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ListId = table.Column<int>(type: "integer", nullable: false),
                    MediaItemId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_list_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_media_list_items_media_items_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_media_list_items_media_lists_ListId",
                        column: x => x.ListId,
                        principalTable: "media_lists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "kodi_library_ids",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kodi_device_id = table.Column<int>(type: "integer", nullable: false),
                    media_item_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    kodi_id = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kodi_library_ids", x => x.id);
                    table.ForeignKey(
                        name: "FK_kodi_library_ids_kodi_devices_kodi_device_id",
                        column: x => x.kodi_device_id,
                        principalTable: "kodi_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_kodi_library_ids_media_items_media_item_id",
                        column: x => x.media_item_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "media_types",
                columns: new[] { "Id", "CastHeading", "CreatedAt", "Description", "DisplayName", "HierarchyLabels", "HierarchyLevels", "InteractionVerb", "IsActive", "IsBuiltIn", "IsTrackable", "Name", "ProgressUnit", "ProviderFamily", "ScanHintsJson", "ScanStrategy", "SupportsCollections" },
                values: new object[,]
                {
                    { 1, null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Television series, seasons, and episodes", "TV Shows", "Show,Season,Episode", 3, "watched", true, true, true, "tv", "minutes", null, null, null, false },
                    { 2, null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Feature films and short films", "Movies", "Movie", 1, "watched", true, true, true, "movies", "minutes", null, null, null, false },
                    { 3, null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Artists, albums, and tracks", "Music", "Artist,Album,Track", 3, "listened", true, true, true, "music", "tracks", null, null, null, false }
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_tokens_Token",
                table: "api_tokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_tokens_UserId",
                table: "api_tokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_background_tasks_PluginId",
                table: "background_tasks",
                column: "PluginId");

            migrationBuilder.CreateIndex(
                name: "IX_device_auth_codes_ApiTokenId",
                table: "device_auth_codes",
                column: "ApiTokenId");

            migrationBuilder.CreateIndex(
                name: "IX_device_auth_codes_Code",
                table: "device_auth_codes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_auth_codes_ExpiresAt",
                table: "device_auth_codes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_device_auth_codes_Status",
                table: "device_auth_codes",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_device_auth_codes_UserId",
                table: "device_auth_codes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_interaction_events_MediaItemId",
                table: "interaction_events",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_interaction_events_Timestamp",
                table: "interaction_events",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_interaction_events_UserId",
                table: "interaction_events",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_interaction_events_UserId_MediaItemId_Timestamp",
                table: "interaction_events",
                columns: new[] { "UserId", "MediaItemId", "Timestamp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_interaction_events_UserId_Timestamp",
                table: "interaction_events",
                columns: new[] { "UserId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "idx_kodi_devices_api_token",
                table: "kodi_devices",
                column: "api_token_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_kodi_devices_user_id",
                table: "kodi_devices",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "idx_kodi_library_ids_unique",
                table: "kodi_library_ids",
                columns: new[] { "kodi_device_id", "media_item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_kodi_library_ids_media_item_id",
                table: "kodi_library_ids",
                column: "media_item_id");

            migrationBuilder.CreateIndex(
                name: "idx_media_credits_item",
                table: "media_credits",
                column: "media_item_id");

            migrationBuilder.CreateIndex(
                name: "idx_media_credits_person",
                table: "media_credits",
                column: "person_name");

            migrationBuilder.CreateIndex(
                name: "idx_media_credits_person_item",
                table: "media_credits",
                column: "person_media_item_id");

            migrationBuilder.CreateIndex(
                name: "idx_media_credits_role",
                table: "media_credits",
                columns: new[] { "role", "person_media_item_id" });

            migrationBuilder.CreateIndex(
                name: "IX_media_enrichment_MediaItemId_PluginId",
                table: "media_enrichment",
                columns: new[] { "MediaItemId", "PluginId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_external_ids_MediaItemId_Source",
                table: "media_external_ids",
                columns: new[] { "MediaItemId", "Source" });

            migrationBuilder.CreateIndex(
                name: "IX_media_external_ids_Source_ExternalId",
                table: "media_external_ids",
                columns: new[] { "Source", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "idx_aliases_alias",
                table: "media_item_aliases",
                column: "alias");

            migrationBuilder.CreateIndex(
                name: "idx_aliases_media_item_id",
                table: "media_item_aliases",
                column: "media_item_id");

            migrationBuilder.CreateIndex(
                name: "idx_dup_candidates_unique",
                table: "media_item_duplicate_candidates",
                columns: new[] { "item_a_id", "item_b_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_item_duplicate_candidates_item_b_id",
                table: "media_item_duplicate_candidates",
                column: "item_b_id");

            migrationBuilder.CreateIndex(
                name: "idx_dup_dismissals_unique",
                table: "media_item_duplicate_dismissals",
                columns: new[] { "item_a_id", "item_b_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_item_duplicate_dismissals_item_b_id",
                table: "media_item_duplicate_dismissals",
                column: "item_b_id");

            migrationBuilder.CreateIndex(
                name: "IX_media_item_known_file_names_FileName",
                table: "media_item_known_file_names",
                column: "FileName");

            migrationBuilder.CreateIndex(
                name: "IX_media_item_known_file_names_MediaItemId_FileName",
                table: "media_item_known_file_names",
                columns: new[] { "MediaItemId", "FileName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_merges_winner_id",
                table: "media_item_merges",
                column: "winner_id");

            migrationBuilder.CreateIndex(
                name: "IX_media_item_related_files_MediaItemId_Path",
                table: "media_item_related_files",
                columns: new[] { "MediaItemId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_media_items_normalized_name",
                table: "media_items",
                column: "normalized_name");

            migrationBuilder.CreateIndex(
                name: "idx_media_items_normalized_name_loose",
                table: "media_items",
                column: "normalized_name_loose");

            migrationBuilder.CreateIndex(
                name: "idx_media_items_normalized_name_loose_mediatypeid",
                table: "media_items",
                columns: new[] { "normalized_name_loose", "MediaTypeId" });

            migrationBuilder.CreateIndex(
                name: "idx_media_items_normalized_name_mediatypeid",
                table: "media_items",
                columns: new[] { "normalized_name", "MediaTypeId" });

            migrationBuilder.CreateIndex(
                name: "IX_media_items_MediaTypeId",
                table: "media_items",
                column: "MediaTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_media_items_Name",
                table: "media_items",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_media_items_ParentId",
                table: "media_items",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_media_list_items_ListId",
                table: "media_list_items",
                column: "ListId");

            migrationBuilder.CreateIndex(
                name: "IX_media_list_items_ListId_MediaItemId",
                table: "media_list_items",
                columns: new[] { "ListId", "MediaItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_list_items_MediaItemId",
                table: "media_list_items",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_media_lists_UserId",
                table: "media_lists",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_media_types_Name",
                table: "media_types",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_UserId_Kind_DedupeKey",
                table: "notifications",
                columns: new[] { "UserId", "Kind", "DedupeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_UserId_ReadAt_CreatedAt",
                table: "notifications",
                columns: new[] { "UserId", "ReadAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_TokenHash",
                table: "password_reset_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_UserId",
                table: "password_reset_tokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "idx_person_headshots_person",
                table: "person_headshots",
                column: "person_media_item_id");

            migrationBuilder.CreateIndex(
                name: "idx_person_headshots_unique",
                table: "person_headshots",
                columns: new[] { "person_media_item_id", "url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_person_provider_credits_unique",
                table: "person_provider_credits",
                columns: new[] { "person_media_item_id", "source", "external_id", "role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plugins_PluginId",
                table: "plugins",
                column: "PluginId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_scan_folders_MediaTypeId",
                table: "scan_folders",
                column: "MediaTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_user_contacts_UserId_Kind",
                table: "user_contacts",
                columns: new[] { "UserId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_user_libraries_MediaItemId",
                table: "user_libraries",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_user_libraries_Status",
                table: "user_libraries",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_user_libraries_UserId_MediaItemId",
                table: "user_libraries",
                columns: new[] { "UserId", "MediaItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_Handle",
                table: "users",
                column: "Handle");

            migrationBuilder.CreateIndex(
                name: "IX_users_Username",
                table: "users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_settings");

            migrationBuilder.DropTable(
                name: "background_tasks");

            migrationBuilder.DropTable(
                name: "device_auth_codes");

            migrationBuilder.DropTable(
                name: "interaction_events");

            migrationBuilder.DropTable(
                name: "kodi_library_ids");

            migrationBuilder.DropTable(
                name: "kodi_scan_acks");

            migrationBuilder.DropTable(
                name: "media_credits");

            migrationBuilder.DropTable(
                name: "media_enrichment");

            migrationBuilder.DropTable(
                name: "media_external_ids");

            migrationBuilder.DropTable(
                name: "media_item_aliases");

            migrationBuilder.DropTable(
                name: "media_item_duplicate_candidates");

            migrationBuilder.DropTable(
                name: "media_item_duplicate_dismissals");

            migrationBuilder.DropTable(
                name: "media_item_known_file_names");

            migrationBuilder.DropTable(
                name: "media_item_merges");

            migrationBuilder.DropTable(
                name: "media_item_related_files");

            migrationBuilder.DropTable(
                name: "media_list_items");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "password_reset_tokens");

            migrationBuilder.DropTable(
                name: "person_headshots");

            migrationBuilder.DropTable(
                name: "person_provider_credits");

            migrationBuilder.DropTable(
                name: "scan_folders");

            migrationBuilder.DropTable(
                name: "user_contacts");

            migrationBuilder.DropTable(
                name: "user_libraries");

            migrationBuilder.DropTable(
                name: "plugins");

            migrationBuilder.DropTable(
                name: "kodi_devices");

            migrationBuilder.DropTable(
                name: "media_lists");

            migrationBuilder.DropTable(
                name: "media_items");

            migrationBuilder.DropTable(
                name: "api_tokens");

            migrationBuilder.DropTable(
                name: "media_types");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
