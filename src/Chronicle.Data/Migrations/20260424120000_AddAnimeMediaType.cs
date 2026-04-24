using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    [DbContext(typeof(ChronicleDbContext))]
    [Migration("20260424120000_AddAnimeMediaType")]
    public partial class AddAnimeMediaType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO media_types (Id, CreatedAt, Description, DisplayName,
                    HierarchyLabels, HierarchyLevels, InteractionVerb,
                    IsActive, IsBuiltIn, Name, ProgressUnit)
                VALUES (5, '2026-01-01 00:00:00', 'Anime series, seasons, and episodes',
                    'Anime', 'Show,Season,Episode', 3, 'watched', 1, 1, 'anime', 'episodes');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM media_types WHERE Id = 5;");
        }
    }
}
