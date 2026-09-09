using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VisiFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerDebt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerDebts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerNumber = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    DebtToCollect = table.Column<decimal>(type: "TEXT", nullable: true),
                    OverdueDebt = table.Column<decimal>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerDebts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerDebts_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDebts_CompanyId_CustomerNumber",
                table: "CustomerDebts",
                columns: new[] { "CompanyId", "CustomerNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerDebts");
        }
    }
}
