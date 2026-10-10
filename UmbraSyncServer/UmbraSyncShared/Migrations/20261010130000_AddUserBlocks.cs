using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MareDbContext))]
    [Migration("20261010130000_AddUserBlocks")]
    public partial class AddUserBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Liste noire côté serveur : une demande d'appairage n'est jamais livrée entre deux
            // utilisateurs dont l'un a bloqué l'autre. Supprimer un compte supprime ses blocages.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS user_blocks (
                    user_uid character varying(10) NOT NULL,
                    blocked_user_uid character varying(10) NOT NULL,
                    created_at timestamp with time zone NOT NULL,
                    CONSTRAINT pk_user_blocks PRIMARY KEY (user_uid, blocked_user_uid),
                    CONSTRAINT fk_user_blocks_users_user_uid
                        FOREIGN KEY (user_uid) REFERENCES users (uid) ON DELETE CASCADE,
                    CONSTRAINT fk_user_blocks_users_blocked_user_uid
                        FOREIGN KEY (blocked_user_uid) REFERENCES users (uid) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_user_blocks_blocked_user_uid
                    ON user_blocks (blocked_user_uid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_blocks");
        }
    }
}
