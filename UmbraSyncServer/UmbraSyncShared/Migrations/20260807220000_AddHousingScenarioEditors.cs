using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class AddHousingScenarioEditors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS housing_scenario_allowed_editors (
                    share_id uuid NOT NULL,
                    editor_uid character varying(10) NOT NULL,
                    CONSTRAINT pk_housing_scenario_allowed_editors PRIMARY KEY (share_id, editor_uid),
                    CONSTRAINT fk_housing_scenario_allowed_editors_housing_scenarios_share_id
                        FOREIGN KEY (share_id) REFERENCES housing_scenarios (id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_housing_scenario_allowed_editors_editor_uid
                    ON housing_scenario_allowed_editors (editor_uid);

                ALTER TABLE housing_scenarios
                    ADD COLUMN IF NOT EXISTS content_revision integer NOT NULL DEFAULT 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "housing_scenario_allowed_editors");

            migrationBuilder.DropColumn(
                name: "content_revision",
                table: "housing_scenarios");
        }
    }
}