using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class MediaTypeDataDrivenFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Aliases",
                table: "media_types",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CastAreActors",
                table: "media_types",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ImportShape",
                table: "media_types",
                type: "TEXT",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDefaultType",
                table: "media_types",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsFamilyDefault",
                table: "media_types",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPersonType",
                table: "media_types",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "Aliases", "CastAreActors", "ImportShape", "IsFamilyDefault" },
                values: new object[] { "tv_show,tv_episode,show,episode", true, "episodic", true });

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "Aliases", "CastAreActors", "ImportShape", "IsDefaultType", "IsFamilyDefault" },
                values: new object[] { "movie,film", true, null, true, true });

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "Aliases", "CastHeading", "ImportShape", "IsFamilyDefault" },
                values: new object[] { "track,song", "Band Members", "artist-album-track", true });

            // Existing databases: the rules that used to be spelled in code are now flags on the type rows. This is the one place the
            // historical names are written, as a one-time data fix; after it, Chronicle never reads a type's name to decide anything.
            migrationBuilder.Sql("UPDATE media_types SET IsPersonType = 1, IsTrackable = 0 WHERE Name = 'people'");
            migrationBuilder.Sql("UPDATE media_types SET CastAreActors = 1 WHERE ProviderFamily IN ('movie', 'tv')");
            migrationBuilder.Sql("UPDATE media_types SET ImportShape = 'episodic', Aliases = COALESCE(Aliases, 'anime_episode') WHERE Name = 'anime' AND ImportShape IS NULL");
            migrationBuilder.Sql("UPDATE media_types SET ImportShape = 'author-series-work' WHERE Name IN ('books', 'audiobooks') AND ImportShape IS NULL");
            migrationBuilder.Sql("UPDATE media_types SET Aliases = 'book' WHERE Name = 'books' AND Aliases IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Aliases",
                table: "media_types");

            migrationBuilder.DropColumn(
                name: "CastAreActors",
                table: "media_types");

            migrationBuilder.DropColumn(
                name: "ImportShape",
                table: "media_types");

            migrationBuilder.DropColumn(
                name: "IsDefaultType",
                table: "media_types");

            migrationBuilder.DropColumn(
                name: "IsFamilyDefault",
                table: "media_types");

            migrationBuilder.DropColumn(
                name: "IsPersonType",
                table: "media_types");

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 3,
                column: "CastHeading",
                value: null);
        }
    }
}
