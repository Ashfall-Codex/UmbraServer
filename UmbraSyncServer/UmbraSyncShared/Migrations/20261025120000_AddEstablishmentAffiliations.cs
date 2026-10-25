using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MareDbContext))]
    [Migration("20261025120000_AddEstablishmentAffiliations")]
    public partial class AddEstablishmentAffiliations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Personnages affiliés à un établissement sans en être propriétaires : la fiche RP, le compte
            // et l'établissement supprimés emportent leurs affiliations.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS establishment_affiliations (
                    id uuid NOT NULL,
                    establishment_id uuid NOT NULL,
                    rp_profile_id integer NOT NULL,
                    user_uid character varying(10) NOT NULL,
                    status integer NOT NULL,
                    show_on_profile boolean NOT NULL,
                    message character varying(200) NULL,
                    requested_utc timestamp with time zone NOT NULL,
                    responded_utc timestamp with time zone NULL,
                    CONSTRAINT pk_establishment_affiliations PRIMARY KEY (id),
                    CONSTRAINT fk_establishment_affiliations_establishments_establishment_id
                        FOREIGN KEY (establishment_id) REFERENCES establishments (id) ON DELETE CASCADE,
                    CONSTRAINT fk_establishment_affiliations_character_rp_profiles_rp_profile
                        FOREIGN KEY (rp_profile_id) REFERENCES character_rp_profiles (id) ON DELETE CASCADE,
                    CONSTRAINT fk_establishment_affiliations_users_user_uid
                        FOREIGN KEY (user_uid) REFERENCES users (uid) ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ix_establishment_affiliations_establishment_id_rp_profile_id
                    ON establishment_affiliations (establishment_id, rp_profile_id);
                CREATE INDEX IF NOT EXISTS ix_establishment_affiliations_rp_profile_id
                    ON establishment_affiliations (rp_profile_id);
                CREATE INDEX IF NOT EXISTS ix_establishment_affiliations_user_uid
                    ON establishment_affiliations (user_uid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "establishment_affiliations");
        }
    }
}
