using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class AddStageShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stage_shares",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    stage_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    stage_format_version = table.Column<int>(type: "integer", nullable: false),
                    world_id = table.Column<long>(type: "bigint", nullable: false),
                    territory_id = table.Column<int>(type: "integer", nullable: false),
                    ward_id = table.Column<int>(type: "integer", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    house_id = table.Column<int>(type: "integer", nullable: false),
                    room_id = table.Column<int>(type: "integer", nullable: false),
                    cipher_data = table.Column<byte[]>(type: "bytea", nullable: true),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: true),
                    salt = table.Column<byte[]>(type: "bytea", nullable: true),
                    tag = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stage_shares", x => x.id);
                    table.ForeignKey(
                        name: "fk_stage_shares_users_owner_uid",
                        column: x => x.owner_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stage_share_allowed_groups",
                columns: table => new
                {
                    share_id = table.Column<Guid>(type: "uuid", nullable: false),
                    allowed_group_gid = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stage_share_allowed_groups", x => new { x.share_id, x.allowed_group_gid });
                    table.ForeignKey(
                        name: "fk_stage_share_allowed_groups_stage_shares_share_id",
                        column: x => x.share_id,
                        principalTable: "stage_shares",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stage_share_allowed_users",
                columns: table => new
                {
                    share_id = table.Column<Guid>(type: "uuid", nullable: false),
                    allowed_individual_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stage_share_allowed_users", x => new { x.share_id, x.allowed_individual_uid });
                    table.ForeignKey(
                        name: "fk_stage_share_allowed_users_stage_shares_share_id",
                        column: x => x.share_id,
                        principalTable: "stage_shares",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stage_share_allowed_groups_allowed_group_gid",
                table: "stage_share_allowed_groups",
                column: "allowed_group_gid");

            migrationBuilder.CreateIndex(
                name: "ix_stage_share_allowed_users_allowed_individual_uid",
                table: "stage_share_allowed_users",
                column: "allowed_individual_uid");

            migrationBuilder.CreateIndex(
                name: "ix_stage_shares_owner_uid",
                table: "stage_shares",
                column: "owner_uid");

            migrationBuilder.CreateIndex(
                name: "ix_stage_shares_world_id_territory_id_ward_id_division_id_hous",
                table: "stage_shares",
                columns: new[] { "world_id", "territory_id", "ward_id", "division_id", "house_id", "room_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stage_share_allowed_groups");

            migrationBuilder.DropTable(
                name: "stage_share_allowed_users");

            migrationBuilder.DropTable(
                name: "stage_shares");
        }
    }
}
