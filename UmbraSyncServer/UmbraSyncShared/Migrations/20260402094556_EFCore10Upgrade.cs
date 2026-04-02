using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class EFCore10Upgrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "establishments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    category = table.Column<int>(type: "integer", nullable: false),
                    languages = table.Column<string[]>(type: "text[]", nullable: true),
                    tags = table.Column<string[]>(type: "text[]", nullable: true),
                    faction_tag = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    schedule = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_public = table.Column<bool>(type: "boolean", nullable: false),
                    created_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    location_type = table.Column<int>(type: "integer", nullable: false),
                    territory_id = table.Column<long>(type: "bigint", nullable: false),
                    server_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    ward_id = table.Column<long>(type: "bigint", nullable: true),
                    plot_id = table.Column<long>(type: "bigint", nullable: true),
                    division_id = table.Column<long>(type: "bigint", nullable: true),
                    is_apartment = table.Column<bool>(type: "boolean", nullable: true),
                    room_id = table.Column<long>(type: "bigint", nullable: true),
                    x = table.Column<float>(type: "real", nullable: true),
                    y = table.Column<float>(type: "real", nullable: true),
                    z = table.Column<float>(type: "real", nullable: true),
                    radius = table.Column<float>(type: "real", nullable: true),
                    logo_image_base64 = table.Column<string>(type: "text", nullable: true),
                    banner_image_base64 = table.Column<string>(type: "text", nullable: true),
                    manager_rp_profile_id = table.Column<int>(type: "integer", nullable: true),
                    show_manager_on_profile = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_establishments", x => x.id);
                    table.ForeignKey(
                        name: "fk_establishments_character_rp_profiles_manager_rp_profile_id",
                        column: x => x.manager_rp_profile_id,
                        principalTable: "character_rp_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_establishments_users_owner_uid",
                        column: x => x.owner_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "establishment_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    establishment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    starts_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ends_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    recurrence = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_establishment_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_establishment_events_establishments_establishment_id",
                        column: x => x.establishment_id,
                        principalTable: "establishments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_establishment_events_establishment_id",
                table: "establishment_events",
                column: "establishment_id");

            migrationBuilder.CreateIndex(
                name: "ix_establishments_manager_rp_profile_id",
                table: "establishments",
                column: "manager_rp_profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_establishments_owner_uid",
                table: "establishments",
                column: "owner_uid");

            migrationBuilder.CreateIndex(
                name: "ix_establishments_territory_id_location_type",
                table: "establishments",
                columns: new[] { "territory_id", "location_type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "establishment_events");

            migrationBuilder.DropTable(
                name: "establishments");
        }
    }
}
