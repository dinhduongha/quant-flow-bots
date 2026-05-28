using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuantFlowBots.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WhaleAlertSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WhaleAlertBotToken",
                schema: "qfb",
                table: "user_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhaleAlertChatId",
                schema: "qfb",
                table: "user_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WhaleAlertCooldownMinutes",
                schema: "qfb",
                table: "user_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WhaleAlertDirection",
                schema: "qfb",
                table: "user_settings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "WhaleAlertEnabled",
                schema: "qfb",
                table: "user_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WhaleAlertIntervals",
                schema: "qfb",
                table: "user_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WhaleAlertMinVolume24h",
                schema: "qfb",
                table: "user_settings",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "WhaleAlertMode",
                schema: "qfb",
                table: "user_settings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "WhaleAlertMultiplier",
                schema: "qfb",
                table: "user_settings",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WhaleAlertBotToken",
                schema: "qfb",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "WhaleAlertChatId",
                schema: "qfb",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "WhaleAlertCooldownMinutes",
                schema: "qfb",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "WhaleAlertDirection",
                schema: "qfb",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "WhaleAlertEnabled",
                schema: "qfb",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "WhaleAlertIntervals",
                schema: "qfb",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "WhaleAlertMinVolume24h",
                schema: "qfb",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "WhaleAlertMode",
                schema: "qfb",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "WhaleAlertMultiplier",
                schema: "qfb",
                table: "user_settings");
        }
    }
}
