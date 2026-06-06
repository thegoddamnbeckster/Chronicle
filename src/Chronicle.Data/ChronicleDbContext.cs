using Chronicle.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Data
{
    public class ChronicleDbContext : DbContext
    {
        public ChronicleDbContext(DbContextOptions<ChronicleDbContext> options)
            : base(options)
        {
        }

        public DbSet<User> Users => Set<User>();
        public DbSet<MediaType> MediaTypes => Set<MediaType>();
        public DbSet<MediaItem> MediaItems => Set<MediaItem>();
        public DbSet<MediaExternalId> MediaExternalIds => Set<MediaExternalId>();
        public DbSet<UserLibrary> UserLibraries => Set<UserLibrary>();
        public DbSet<InteractionEvent> InteractionEvents => Set<InteractionEvent>();
        public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
        public DbSet<Plugin> Plugins => Set<Plugin>();
        public DbSet<MediaList> MediaLists => Set<MediaList>();
        public DbSet<MediaListItem> MediaListItems => Set<MediaListItem>();
        public DbSet<DeviceAuthCode> DeviceAuthCodes => Set<DeviceAuthCode>();
        public DbSet<AppSetting> AppSettings => Set<AppSetting>();
        public DbSet<BackgroundTask> BackgroundTasks => Set<BackgroundTask>();
        public DbSet<ScanFolder> ScanFolders => Set<ScanFolder>();
        public DbSet<MediaItemEnrichment> MediaEnrichments { get; set; } = null!;
        public DbSet<MediaCredit> MediaCredits { get; set; } = null!;
        public DbSet<MediaItemAlias> MediaItemAliases { get; set; } = null!;
        public DbSet<MediaItemMerge> MediaItemMerges { get; set; } = null!;
        public DbSet<MediaItemDuplicateCandidate> MediaItemDuplicateCandidates { get; set; } = null!;
        public DbSet<MediaItemDuplicateDismissal> MediaItemDuplicateDismissals { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("users");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Username).IsUnique();
                entity.HasIndex(e => e.Email).IsUnique();
                entity.Property(e => e.Username).IsRequired().HasMaxLength(50);
                entity.Property(e => e.PasswordHash).IsRequired();
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(u => u.PreferencesJson)
                    .HasColumnName("preferences_json")
                    .HasDefaultValue("{}");
            });

            modelBuilder.Entity<MediaType>(entity =>
            {
                entity.ToTable("media_types");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Name).IsUnique();
                entity.Property(e => e.Name).IsRequired().HasMaxLength(50);
                entity.Property(e => e.DisplayName).IsRequired();
                entity.Property(e => e.InteractionVerb).HasDefaultValue("watched");
                entity.Property(e => e.ProgressUnit).HasDefaultValue("minutes");
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                // Seed: built-in media types
                entity.HasData(
                    new MediaType
                    {
                        Id = 1,
                        Name = "tv",
                        DisplayName = "TV Shows",
                        Description = "Television series, seasons, and episodes",
                        HierarchyLevels = 3,
                        HierarchyLabels = "Show,Season,Episode",
                        InteractionVerb = "watched",
                        ProgressUnit = "minutes",
                        IsBuiltIn = true,
                        IsActive = true,
                        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    },
                    new MediaType
                    {
                        Id = 2,
                        Name = "movies",
                        DisplayName = "Movies",
                        Description = "Feature films and short films",
                        HierarchyLevels = 1,
                        HierarchyLabels = "Movie",
                        InteractionVerb = "watched",
                        ProgressUnit = "minutes",
                        IsBuiltIn = true,
                        IsActive = true,
                        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    },
                    new MediaType
                    {
                        Id = 3,
                        Name = "music",
                        DisplayName = "Music",
                        Description = "Artists, albums, and tracks",
                        HierarchyLevels = 3,
                        HierarchyLabels = "Artist,Album,Track",
                        InteractionVerb = "listened",
                        ProgressUnit = "tracks",
                        IsBuiltIn = true,
                        IsActive = true,
                        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    });
            });

            modelBuilder.Entity<MediaItem>(entity =>
            {
                entity.ToTable("media_items");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.MediaTypeId);
                entity.HasIndex(e => e.Name);
                entity.HasIndex(e => e.ParentId);
                entity.Property(e => e.Name).IsRequired();
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(e => e.MediaType)
                    .WithMany()
                    .HasForeignKey(e => e.MediaTypeId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(e => e.Parent)
                    .WithMany(e => e.Children)
                    .HasForeignKey(e => e.ParentId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<MediaExternalId>(entity =>
            {
                entity.ToTable("media_external_ids");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.MediaItemId, e.Source });
                entity.HasIndex(e => new { e.Source, e.ExternalId });
                entity.Property(e => e.Source).IsRequired().HasMaxLength(50);
                entity.Property(e => e.ExternalId).IsRequired();

                entity.HasOne(e => e.MediaItem)
                    .WithMany(e => e.ExternalIds)
                    .HasForeignKey(e => e.MediaItemId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<UserLibrary>(entity =>
            {
                entity.ToTable("user_libraries");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.UserId, e.MediaItemId }).IsUnique();
                entity.HasIndex(e => e.Status);
                entity.Property(e => e.Status).HasConversion<string>();
                entity.Property(e => e.AddedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(e => e.User)
                    .WithMany()
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(e => e.MediaItem)
                    .WithMany()
                    .HasForeignKey(e => e.MediaItemId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<InteractionEvent>(entity =>
            {
                entity.ToTable("interaction_events");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.UserId);
                entity.HasIndex(e => e.MediaItemId);
                entity.HasIndex(e => e.Timestamp);
                entity.HasIndex(e => new { e.UserId, e.Timestamp });
                // Idempotency guard: prevent duplicate scrobbles for the same user/item/time.
                entity.HasIndex(e => new { e.UserId, e.MediaItemId, e.Timestamp }).IsUnique();
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(e => e.User)
                    .WithMany()
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(e => e.MediaItem)
                    .WithMany()
                    .HasForeignKey(e => e.MediaItemId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<ApiToken>(entity =>
            {
                entity.ToTable("api_tokens");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Token).IsUnique();
                entity.HasIndex(e => e.UserId);
                entity.Property(e => e.Token).IsRequired();
                entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(e => e.User)
                    .WithMany()
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<Plugin>(entity =>
            {
                entity.ToTable("plugins");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.PluginId).IsUnique();
                entity.Property(e => e.PluginId).IsRequired().HasMaxLength(200);
                entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
                entity.Property(e => e.Version).IsRequired().HasMaxLength(50);
                entity.Property(e => e.Author).IsRequired().HasMaxLength(200);
                entity.Property(e => e.DllPath).IsRequired();
                entity.Property(e => e.InstalledAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            });

            modelBuilder.Entity<MediaList>(entity =>
            {
                entity.ToTable("media_lists");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.UserId);
                entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(e => e.User)
                    .WithMany()
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<MediaListItem>(entity =>
            {
                entity.ToTable("media_list_items");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.ListId, e.MediaItemId }).IsUnique();
                entity.HasIndex(e => e.ListId);
                entity.Property(e => e.AddedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(e => e.List)
                    .WithMany(e => e.Items)
                    .HasForeignKey(e => e.ListId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(e => e.MediaItem)
                    .WithMany()
                    .HasForeignKey(e => e.MediaItemId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<AppSetting>(e =>
            {
                e.ToTable("app_settings");
                e.HasKey(s => s.Key);
                e.Property(s => s.Key).HasMaxLength(200);
                e.Property(s => s.Value).IsRequired();
            });

            modelBuilder.Entity<BackgroundTask>(e =>
            {
                e.ToTable("background_tasks");
                e.HasKey(t => t.TaskId);
                e.Property(t => t.TaskId).HasMaxLength(100);
                e.Property(t => t.DisplayName).IsRequired();
                e.Property(t => t.Description).IsRequired();
                e.Property(t => t.CronExpression).IsRequired();
                e.HasOne(t => t.Plugin)
                 .WithMany()
                 .HasForeignKey(t => t.PluginId)
                 .HasPrincipalKey(p => p.PluginId)
                 .OnDelete(DeleteBehavior.Cascade)
                 .IsRequired(false);
            });

            modelBuilder.Entity<ScanFolder>(e =>
            {
                e.ToTable("scan_folders");
                e.HasKey(f => f.Id);
                e.Property(f => f.Path).IsRequired().HasMaxLength(1000);
                e.Property(f => f.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                e.HasOne(f => f.MediaType)
                 .WithMany()
                 .HasForeignKey(f => f.MediaTypeId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<DeviceAuthCode>(entity =>
            {
                entity.ToTable("device_auth_codes");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Code).IsUnique();
                entity.HasIndex(e => e.Status);
                entity.HasIndex(e => e.ExpiresAt);
                entity.Property(e => e.Code).IsRequired().HasMaxLength(32);
                entity.Property(e => e.DisplayCode).IsRequired().HasMaxLength(9);
                entity.Property(e => e.Status).HasConversion<string>();
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(e => e.User)
                    .WithMany()
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.SetNull);

                entity.HasOne(e => e.ApiToken)
                    .WithMany()
                    .HasForeignKey(e => e.ApiTokenId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<MediaItemEnrichment>(e =>
            {
                e.ToTable("media_enrichment");
                e.HasKey(x => x.Id);
                e.HasIndex(x => new { x.MediaItemId, x.PluginId }).IsUnique();
                e.Property(x => x.Status).HasConversion<string>();
                e.HasOne(x => x.MediaItem)
                 .WithMany()
                 .HasForeignKey(x => x.MediaItemId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<MediaCredit>(e =>
            {
                e.ToTable("media_credits");
                e.HasKey(c => c.Id);
                e.Property(c => c.Id).HasColumnName("id");
                e.Property(c => c.MediaItemId).HasColumnName("media_item_id");
                e.Property(c => c.PersonName).HasColumnName("person_name");
                e.Property(c => c.Role).HasColumnName("role");
                e.Property(c => c.CharacterName).HasColumnName("character_name");
                e.Property(c => c.BillingOrder).HasColumnName("billing_order");
                e.Property(c => c.Source).HasColumnName("source");
                e.Property(c => c.ExternalPersonId).HasColumnName("external_person_id");
                e.Property(c => c.CreatedAt).HasColumnName("created_at");

                e.HasOne(c => c.MediaItem)
                 .WithMany()
                 .HasForeignKey(c => c.MediaItemId)
                 .OnDelete(DeleteBehavior.Cascade);

                e.HasIndex(c => c.MediaItemId).HasDatabaseName("idx_media_credits_item");
                e.HasIndex(c => c.PersonName).HasDatabaseName("idx_media_credits_person");
            });

            // NormalizedName on MediaItem
            modelBuilder.Entity<MediaItem>(e =>
            {
                e.Property(x => x.NormalizedName).HasColumnName("normalized_name");
                e.HasIndex(x => x.NormalizedName).HasDatabaseName("idx_media_items_normalized_name");
            });

            modelBuilder.Entity<MediaItemAlias>(e =>
            {
                e.ToTable("media_item_aliases");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
                e.Property(x => x.MediaItemId).HasColumnName("media_item_id").IsRequired();
                e.Property(x => x.Alias).HasColumnName("alias").IsRequired();
                e.Property(x => x.Source).HasColumnName("source").IsRequired();
                e.Property(x => x.CreatedAt).HasColumnName("created_at");
                e.HasIndex(x => x.MediaItemId).HasDatabaseName("idx_aliases_media_item_id");
                e.HasIndex(x => x.Alias).HasDatabaseName("idx_aliases_alias");
                e.HasOne(x => x.MediaItem).WithMany(m => m.Aliases)
                    .HasForeignKey(x => x.MediaItemId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<MediaItemMerge>(e =>
            {
                e.ToTable("media_item_merges");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
                e.Property(x => x.WinnerId).HasColumnName("winner_id").IsRequired();
                e.Property(x => x.LoserOriginalId).HasColumnName("loser_original_id").IsRequired();
                e.Property(x => x.LoserName).HasColumnName("loser_name").IsRequired();
                e.Property(x => x.LoserMediaTypeId).HasColumnName("loser_media_type_id").IsRequired();
                e.Property(x => x.LoserHierarchyLevel).HasColumnName("loser_hierarchy_level").IsRequired();
                e.Property(x => x.LoserParentId).HasColumnName("loser_parent_id");
                e.Property(x => x.LoserExternalIdsJson).HasColumnName("loser_external_ids_json").HasDefaultValue("[]");
                e.Property(x => x.LoserChildIdsJson).HasColumnName("loser_child_ids_json").HasDefaultValue("[]");
                e.Property(x => x.MergedAt).HasColumnName("merged_at");
                e.Property(x => x.MergedByUserId).HasColumnName("merged_by_user_id");
                e.HasIndex(x => x.WinnerId).HasDatabaseName("idx_merges_winner_id");
                e.HasOne(x => x.Winner).WithMany(m => m.MergesAsWinner)
                    .HasForeignKey(x => x.WinnerId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<MediaItemDuplicateCandidate>(e =>
            {
                e.ToTable("media_item_duplicate_candidates");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
                e.Property(x => x.ItemAId).HasColumnName("item_a_id").IsRequired();
                e.Property(x => x.ItemBId).HasColumnName("item_b_id").IsRequired();
                e.Property(x => x.DetectedAt).HasColumnName("detected_at");
                e.HasIndex(x => new { x.ItemAId, x.ItemBId }).IsUnique()
                    .HasDatabaseName("idx_dup_candidates_unique");
                e.HasOne(x => x.ItemA).WithMany().HasForeignKey(x => x.ItemAId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne(x => x.ItemB).WithMany().HasForeignKey(x => x.ItemBId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<MediaItemDuplicateDismissal>(e =>
            {
                e.ToTable("media_item_duplicate_dismissals");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
                e.Property(x => x.ItemAId).HasColumnName("item_a_id").IsRequired();
                e.Property(x => x.ItemBId).HasColumnName("item_b_id").IsRequired();
                e.Property(x => x.DismissedAt).HasColumnName("dismissed_at");
                e.HasIndex(x => new { x.ItemAId, x.ItemBId }).IsUnique()
                    .HasDatabaseName("idx_dup_dismissals_unique");
                // Cascade-delete dismissals when either referenced item is deleted so they
                // don't accumulate as orphaned rows indefinitely.
                e.HasOne<MediaItem>().WithMany().HasForeignKey(x => x.ItemAId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne<MediaItem>().WithMany().HasForeignKey(x => x.ItemBId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
