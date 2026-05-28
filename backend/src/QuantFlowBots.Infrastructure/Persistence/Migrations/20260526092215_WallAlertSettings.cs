using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuantFlowBots.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WallAlertSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WallAlertBotToken",
                schema: "qfb",
                table: "user_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WallAlertChatId",
                schema: "qfb",
                table: "user_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WallAlertCooldownMinutes",
                schema: "qfb",
                table: "user_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "WallAlertEnabled",
                schema: "qfb",
                table: "user_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "WallAlertMaxDistancePct",
                schema: "qfb",
                table: "user_settings",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "WallAlertMinNotional",
                schema: "qfb",
                table: "user_settings",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "WallAlertSide",
                schema: "qfb",
                table: "user_settings",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WallAlertBotToken",
                schema: "qfb",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "WallAlertChatId",
                schema: "qfb",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "WallAlertCooldownMinutes",
                schema: "qfb",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "WallAlertEnabled",
                schema: "qfb",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "WallAlertMaxDistancePct",
                schema: "qfb",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "WallAlertMinNotional",
                schema: "qfb",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "WallAlertSide",
                schema: "qfb",
                table: "user_settings");
        }
    }
}
