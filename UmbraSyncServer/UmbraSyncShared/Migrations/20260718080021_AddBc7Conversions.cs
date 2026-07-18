using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class AddBc7Conversions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS file_bc7_conversions (
                    source_hash character varying(40) NOT NULL,
                    role integer NOT NULL,
                    state integer NOT NULL,
                    alternate_hash character varying(40),
                    updated_at timestamp with time zone NOT NULL,
                    CONSTRAINT pk_file_bc7_conversions PRIMARY KEY (source_hash)
                );
                CREATE INDEX IF NOT EXISTS ix_file_bc7_conversions_alternate_hash ON file_bc7_conversions (alternate_hash);
                CREATE INDEX IF NOT EXISTS ix_file_bc7_conversions_state ON file_bc7_conversions (state);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "file_bc7_conversions");
        }
    }
}
