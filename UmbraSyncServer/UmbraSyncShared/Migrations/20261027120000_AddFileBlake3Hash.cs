using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MareDbContext))]
    [Migration("20261027120000_AddFileBlake3Hash")]
    public partial class AddFileBlake3Hash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // BLAKE3 du contenu, calculé par le serveur à l'upload et à la conversion BC7. Nullable : les
            // fichiers déjà en place n'en ont pas tant qu'ils ne sont pas repassés par l'un de ces chemins.
            migrationBuilder.Sql("""
                ALTER TABLE file_caches
                    ADD COLUMN IF NOT EXISTS blake3hash character varying(64) NULL;
                CREATE INDEX IF NOT EXISTS ix_file_caches_blake3hash
                    ON file_caches (blake3hash);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ix_file_caches_blake3hash", table: "file_caches");
            migrationBuilder.DropColumn(name: "blake3hash", table: "file_caches");
        }
    }
}
