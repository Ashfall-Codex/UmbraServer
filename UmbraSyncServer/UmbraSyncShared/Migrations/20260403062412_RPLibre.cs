using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class RPLibre : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wild_rp_announcements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    world_id = table.Column<long>(type: "bigint", nullable: false),
                    territory_id = table.Column<long>(type: "bigint", nullable: false),
                    message = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    rp_profile_id = table.Column<int>(type: "integer", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wild_rp_announcements", x => x.id);
                    table.ForeignKey(
                        name: "fk_wild_rp_announcements_character_rp_profiles_rp_profile_id",
                        column: x => x.rp_profile_id,
                        principalTable: "character_rp_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_wild_rp_announcements_users_user_uid",
                        column: x => x.user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_wild_rp_announcements_expires_at_utc",
                table: "wild_rp_announcements",
                column: "expires_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_wild_rp_announcements_rp_profile_id",
                table: "wild_rp_announcements",
                column: "rp_profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_wild_rp_announcements_user_uid",
                table: "wild_rp_announcements",
                column: "user_uid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_wild_rp_announcements_world_id",
                table: "wild_rp_announcements",
                column: "world_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wild_rp_announcements");
        }
    }
}
