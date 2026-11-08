using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class AddDecorCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "decor_catalog_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    preview_image_base64 = table.Column<string>(type: "text", nullable: true),
                    preview_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    stage_format_version = table.Column<int>(type: "integer", nullable: false),
                    file_count = table.Column<int>(type: "integer", nullable: false),
                    total_bytes = table.Column<long>(type: "bigint", nullable: false),
                    anchor_x = table.Column<float>(type: "real", nullable: false),
                    anchor_y = table.Column<float>(type: "real", nullable: false),
                    anchor_z = table.Column<float>(type: "real", nullable: false),
                    anchor_territory = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: true),
                    display_count = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decor_catalog_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_decor_catalog_entries_users_owner_uid",
                        column: x => x.owner_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "decor_catalog_favorites",
                columns: table => new
                {
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decor_catalog_favorites", x => new { x.entry_id, x.user_uid });
                    table.ForeignKey(
                        name: "fk_decor_catalog_favorites_decor_catalog_entries_entry_id",
                        column: x => x.entry_id,
                        principalTable: "decor_catalog_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_decor_catalog_favorites_users_user_uid",
                        column: x => x.user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "decor_catalog_reports",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporting_user_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    report_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decor_catalog_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_decor_catalog_reports_decor_catalog_entries_entry_id",
                        column: x => x.entry_id,
                        principalTable: "decor_catalog_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_decor_catalog_reports_users_reporting_user_uid",
                        column: x => x.reporting_user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_decor_catalog_entries_created_utc",
                table: "decor_catalog_entries",
                column: "created_utc");

            migrationBuilder.CreateIndex(
                name: "ix_decor_catalog_entries_display_count",
                table: "decor_catalog_entries",
                column: "display_count");

            migrationBuilder.CreateIndex(
                name: "ix_decor_catalog_entries_owner_uid",
                table: "decor_catalog_entries",
                column: "owner_uid");

            migrationBuilder.CreateIndex(
                name: "ix_decor_catalog_favorites_user_uid",
                table: "decor_catalog_favorites",
                column: "user_uid");

            migrationBuilder.CreateIndex(
                name: "ix_decor_catalog_reports_entry_id",
                table: "decor_catalog_reports",
                column: "entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_decor_catalog_reports_reporting_user_uid",
                table: "decor_catalog_reports",
                column: "reporting_user_uid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "decor_catalog_favorites");

            migrationBuilder.DropTable(
                name: "decor_catalog_reports");

            migrationBuilder.DropTable(
                name: "decor_catalog_entries");
        }
    }
}
