using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MareDbContext))]
    [Migration("20261010120000_FileCacheUploaderSetNull")]
    public partial class FileCacheUploaderSetNull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Supprimer un compte ne doit plus effacer les fichiers qu'il a envoyés : ils sont partagés
            // par contenu avec tous les joueurs qui utilisent le même mod. On ne retire que l'attribution.
            migrationBuilder.Sql("""
                ALTER TABLE file_caches DROP CONSTRAINT IF EXISTS fk_file_caches_users_uploader_uid;
                ALTER TABLE file_caches
                    ADD CONSTRAINT fk_file_caches_users_uploader_uid
                    FOREIGN KEY (uploader_uid) REFERENCES users (uid) ON DELETE SET NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE file_caches DROP CONSTRAINT IF EXISTS fk_file_caches_users_uploader_uid;
                ALTER TABLE file_caches
                    ADD CONSTRAINT fk_file_caches_users_uploader_uid
                    FOREIGN KEY (uploader_uid) REFERENCES users (uid);
                """);
        }
    }
}
