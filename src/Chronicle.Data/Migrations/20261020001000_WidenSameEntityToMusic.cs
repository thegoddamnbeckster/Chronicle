using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class WidenSameEntityToMusic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The same-entity rule also covers a music artist who is the same person as a People record (a musician who writes books, an
            // author who records music). Only the untouched starting value is widened, so an administrator's edit is never overwritten.
            migrationBuilder.Sql("UPDATE app_settings SET \"Value\" = '[{\"mediaTypes\":[\"books\",\"audiobooks\",\"music\",\"people\"],\"level\":0,\"requireSameName\":true}]' WHERE \"Key\" = 'enrichment.same_entity_media_types' AND \"Value\" = '[{\"mediaTypes\":[\"books\",\"audiobooks\",\"people\"],\"level\":0,\"requireSameName\":true}]'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE app_settings SET \"Value\" = '[{\"mediaTypes\":[\"books\",\"audiobooks\",\"people\"],\"level\":0,\"requireSameName\":true}]' WHERE \"Key\" = 'enrichment.same_entity_media_types' AND \"Value\" = '[{\"mediaTypes\":[\"books\",\"audiobooks\",\"music\",\"people\"],\"level\":0,\"requireSameName\":true}]'");
        }
    }
}
