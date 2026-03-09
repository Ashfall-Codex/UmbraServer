using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class AddFileS3Confirmed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- S3 columns
                ALTER TABLE file_caches ADD COLUMN IF NOT EXISTS s3confirmed boolean NOT NULL DEFAULT false;
                ALTER TABLE file_caches ADD COLUMN IF NOT EXISTS s3confirmed_at timestamp with time zone;
                CREATE INDEX IF NOT EXISTS ix_file_caches_s3confirmed ON file_caches (s3confirmed);

                -- moodles_data
                ALTER TABLE character_rp_profiles ADD COLUMN IF NOT EXISTS moodles_data text;

                -- housing_share_allowed_groups
                CREATE TABLE IF NOT EXISTS housing_share_allowed_groups (
                    share_id uuid NOT NULL,
                    allowed_group_gid character varying(20) NOT NULL,
                    CONSTRAINT pk_housing_share_allowed_groups PRIMARY KEY (share_id, allowed_group_gid),
                    CONSTRAINT fk_housing_share_allowed_groups_housing_shares_share_id FOREIGN KEY (share_id) REFERENCES housing_shares (id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_housing_share_allowed_groups_allowed_group_gid ON housing_share_allowed_groups (allowed_group_gid);

                -- housing_share_allowed_users
                CREATE TABLE IF NOT EXISTS housing_share_allowed_users (
                    share_id uuid NOT NULL,
                    allowed_individual_uid character varying(10) NOT NULL,
                    CONSTRAINT pk_housing_share_allowed_users PRIMARY KEY (share_id, allowed_individual_uid),
                    CONSTRAINT fk_housing_share_allowed_users_housing_shares_share_id FOREIGN KEY (share_id) REFERENCES housing_shares (id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_housing_share_allowed_users_allowed_individual_uid ON housing_share_allowed_users (allowed_individual_uid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "housing_share_allowed_groups");

            migrationBuilder.DropTable(
                name: "housing_share_allowed_users");

            migrationBuilder.DropIndex(
                name: "ix_file_caches_s3confirmed",
                table: "file_caches");

            migrationBuilder.DropColumn(
                name: "s3confirmed",
                table: "file_caches");

            migrationBuilder.DropColumn(
                name: "s3confirmed_at",
                table: "file_caches");

            migrationBuilder.DropColumn(
                name: "moodles_data",
                table: "character_rp_profiles");
        }
    }
}
