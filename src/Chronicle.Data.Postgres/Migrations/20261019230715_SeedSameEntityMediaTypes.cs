using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SeedSameEntityMediaTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // An author in a book or audiobook library and that author's record in the People catalog are one real person held in two
            // places, so they legitimately share provider ids (Open Library, Hardcover ...). Starting value of the rule that says so; it is a
            // setting, so an administrator can change it and no media type is named in code.
            migrationBuilder.Sql("INSERT INTO app_settings (\"Key\", \"Value\") SELECT 'enrichment.same_entity_media_types', '[{\"mediaTypes\":[\"books\",\"audiobooks\",\"people\"],\"level\":0,\"requireSameName\":true}]' WHERE NOT EXISTS (SELECT 1 FROM app_settings WHERE \"Key\" = 'enrichment.same_entity_media_types')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM app_settings WHERE \"Key\" = 'enrichment.same_entity_media_types'");
        }
    }
}
