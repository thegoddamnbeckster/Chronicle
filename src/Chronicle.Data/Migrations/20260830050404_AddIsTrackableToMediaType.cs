using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIsTrackableToMediaType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTrackable",
                table: "media_types",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 1,
                column: "IsTrackable",
                value: true);

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 2,
                column: "IsTrackable",
                value: true);

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 3,
                column: "IsTrackable",
                value: true);

            // A pre-existing "people" row (created out-of-band before this type had a proper
            // seed -- see PersonResolutionService.GetPeopleMediaTypeIdAsync) is a reference/
            // catalog type, not something a user tracks -- flip it here so LibraryService's
            // auto-track-every-root-item mechanism stops picking it up. A fresh install has no
            // such row yet; GetPeopleMediaTypeIdAsync creates one with IsTrackable already false.
            migrationBuilder.Sql(
                "UPDATE media_types SET IsTrackable = 0 WHERE Name = 'people';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsTrackable",
                table: "media_types");
        }
    }
}
