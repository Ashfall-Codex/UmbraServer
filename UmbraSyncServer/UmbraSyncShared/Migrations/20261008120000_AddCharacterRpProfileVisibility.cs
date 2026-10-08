using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MareDbContext))]
    [Migration("20261008120000_AddCharacterRpProfileVisibility")]
    public partial class AddCharacterRpProfileVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 0 = paires directes et syncshell : le comportement d'avant pour tous les profils existants.
            migrationBuilder.Sql("""
                ALTER TABLE character_rp_profiles
                    ADD COLUMN IF NOT EXISTS visibility integer NOT NULL DEFAULT 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "visibility", table: "character_rp_profiles");
        }
    }
}
