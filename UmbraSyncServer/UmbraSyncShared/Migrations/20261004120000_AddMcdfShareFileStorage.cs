using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MareDbContext))]
    [Migration("20261004120000_AddMcdfShareFileStorage")]
    public partial class AddMcdfShareFileStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE mcdf_shares
                    ADD COLUMN IF NOT EXISTS is_file_backed boolean NOT NULL DEFAULT FALSE,
                    ADD COLUMN IF NOT EXISTS cipher_length bigint NOT NULL DEFAULT 0;
                UPDATE mcdf_shares SET cipher_length = length(cipher_data) WHERE cipher_length = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "is_file_backed", table: "mcdf_shares");
            migrationBuilder.DropColumn(name: "cipher_length", table: "mcdf_shares");
        }
    }
}
