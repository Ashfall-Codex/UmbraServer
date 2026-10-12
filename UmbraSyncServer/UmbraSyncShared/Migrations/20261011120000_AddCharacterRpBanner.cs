using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MareDbContext))]
    [Migration("20261011120000_AddCharacterRpBanner")]
    public partial class AddCharacterRpBanner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE character_rp_profiles
                    ADD COLUMN IF NOT EXISTS rp_banner_base64 text NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "rp_banner_base64", table: "character_rp_profiles");
        }
    }
}
