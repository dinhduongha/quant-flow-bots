using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuantFlowBots.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SymbolRiskFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "symbol_risk_flags",
                schema: "qfb",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_symbol_risk_flags", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_symbol_risk_flags_Symbol",
                schema: "qfb",
                table: "symbol_risk_flags",
                column: "Symbol",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "symbol_risk_flags",
                schema: "qfb");
        }
    }
}
