using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    public partial class UpdateMoviesHierarchyForCollections : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE media_types SET HierarchyLevels = 2, HierarchyLabels = 'Collection,Movie' WHERE Name = 'movies'");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE media_types SET HierarchyLevels = 1, HierarchyLabels = NULL WHERE Name = 'movies'");
        }
    }
}
