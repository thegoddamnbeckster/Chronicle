using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaTypeProviderFamilyAndCastHeading : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CastHeading",
                table: "media_types",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderFamily",
                table: "media_types",
                type: "TEXT",
                maxLength: 30,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CastHeading", "ProviderFamily" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "CastHeading", "ProviderFamily" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "CastHeading", "ProviderFamily" },
                values: new object[] { null, null });
        
            // Start every existing type with what the code used to work out from its name, so nothing changes until
            // an administrator edits it. (Same rules as ProviderFamilies.DefaultFor / DefaultCastHeadingFor.)
            migrationBuilder.Sql("UPDATE media_types SET ProviderFamily = 'movie' WHERE ProviderFamily IS NULL AND LOWER(Name) LIKE '%anime%' AND LOWER(Name) LIKE '%movie%';");
            migrationBuilder.Sql("UPDATE media_types SET ProviderFamily = 'tv' WHERE ProviderFamily IS NULL AND (LOWER(Name) LIKE '%tv%' OR LOWER(Name) LIKE '%show%' OR LOWER(Name) LIKE '%series%' OR LOWER(Name) LIKE '%anime%');");
            migrationBuilder.Sql("UPDATE media_types SET ProviderFamily = 'music' WHERE ProviderFamily IS NULL AND (LOWER(Name) LIKE '%music%' OR LOWER(Name) LIKE '%album%' OR LOWER(Name) LIKE '%track%');");
            migrationBuilder.Sql("UPDATE media_types SET ProviderFamily = 'movie' WHERE ProviderFamily IS NULL AND LOWER(Name) LIKE '%fanedit%';");
            migrationBuilder.Sql("UPDATE media_types SET CastHeading = 'Band Members' WHERE CastHeading IS NULL AND LOWER(Name) = 'music';");
            migrationBuilder.Sql("UPDATE media_types SET CastHeading = 'Narrators' WHERE CastHeading IS NULL AND LOWER(Name) IN ('audiobook', 'audiobooks');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CastHeading",
                table: "media_types");

            migrationBuilder.DropColumn(
                name: "ProviderFamily",
                table: "media_types");
        }
    }
}
