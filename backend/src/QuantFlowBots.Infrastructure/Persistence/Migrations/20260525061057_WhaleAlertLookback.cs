using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuantFlowBots.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WhaleAlertLookback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent: column was added out-of-band on an earlier dev run, so we use raw SQL
            // with IF NOT EXISTS rather than AddColumn (which crashes when the column is already
            // there). Default 20 matches the entity default so legacy rows behave sensibly.
            migrationBuilder.Sql(
                "ALTER TABLE qfb.user_settings ADD COLUMN IF NOT EXISTS \"WhaleAlertLookback\" integer NOT NULL DEFAULT 20;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WhaleAlertLookback",
                schema: "qfb",
                table: "user_settings");
        }
    }
}
