using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaTypeScanStrategyAndUserModified : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsUserModified",
                table: "media_types",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ScanStrategy",
                table: "media_types",
                type: "TEXT",
                maxLength: 30,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 1,
                column: "ScanStrategy",
                value: null);

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 2,
                column: "ScanStrategy",
                value: null);

            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Id",
                keyValue: 3,
                column: "ScanStrategy",
                value: null);

            // Existing databases: the audiobook scan used to be chosen by the type's NAME. Carry that over once, as data.
            migrationBuilder.Sql("UPDATE media_types SET ScanStrategy = 'audiobook' WHERE lower(Name) = 'audiobooks' AND ScanStrategy IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsUserModified",
                table: "media_types");

            migrationBuilder.DropColumn(
                name: "ScanStrategy",
                table: "media_types");
        }
    }
}
