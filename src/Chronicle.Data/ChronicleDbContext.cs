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

                // Seed: TV Shows built-in type
                entity.HasData(new MediaType
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
        }
    }
}
