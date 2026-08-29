using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportsCollectionsToMediaType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SupportsCollections",
                table: "media_types",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Matched by Name, not Id -- media_types rows created at runtime (e.g. "audiobooks")
            // don't have the fixed ids the migration-seeded built-in types do, so an Id-keyed
            // UpdateData would silently miss them. These are the types where a level-0 item is
            // a bucket of distinct works (movies in a Collection, books by an Author) rather
            // than one continuous work with sub-parts (a TV show's seasons/episodes).
            migrationBuilder.Sql(
                "UPDATE media_types SET \"SupportsCollections\" = 1 " +
                "WHERE \"Name\" IN ('movies', 'fanedits', 'anime_movies', 'audiobooks');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupportsCollections",
                table: "media_types");
        }
    }
}
