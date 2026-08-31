using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VisiFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class SplitCustomerByMonthAndVisitStandard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Customers_CompanyId_CustomerNumber",
                table: "Customers");

            // Create the new durable-standard table FIRST, and copy every existing customer's
            // RequiredVisitsPerWeek into it, BEFORE that column is dropped from Customers below -
            // otherwise this data-preserving migration would just delete everyone's visit standard.
            migrationBuilder.CreateTable(
                name: "CustomerVisitStandards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerNumber = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    RequiredVisitsPerWeek = table.Column<decimal>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerVisitStandards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerVisitStandards_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(@"
                INSERT INTO CustomerVisitStandards (CompanyId, CustomerNumber, RequiredVisitsPerWeek, UpdatedAt)
                SELECT CompanyId, CustomerNumber, RequiredVisitsPerWeek, datetime('now')
                FROM Customers
                WHERE RequiredVisitsPerWeek IS NOT NULL;
            ");

            migrationBuilder.DropColumn(
                name: "RequiredVisitsPerWeek",
                table: "Customers");

            // Existing rows predate per-month snapshots entirely - they were all loaded from a single
            // upload today, so backfilling them to the current (year, month) is the correct one-time
            // assignment rather than an arbitrary placeholder.
            migrationBuilder.AddColumn<int>(
                name: "Month",
                table: "Customers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 8);

            migrationBuilder.AddColumn<int>(
                name: "Year",
                table: "Customers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2026);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_CompanyId_CustomerNumber_Year_Month",
                table: "Customers",
                columns: new[] { "CompanyId", "CustomerNumber", "Year", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerVisitStandards_CompanyId_CustomerNumber",
                table: "CustomerVisitStandards",
                columns: new[] { "CompanyId", "CustomerNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerVisitStandards");

            migrationBuilder.DropIndex(
                name: "IX_Customers_CompanyId_CustomerNumber_Year_Month",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Month",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Year",
                table: "Customers");

            migrationBuilder.AddColumn<decimal>(
                name: "RequiredVisitsPerWeek",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_CompanyId_CustomerNumber",
                table: "Customers",
                columns: new[] { "CompanyId", "CustomerNumber" },
                unique: true);
        }
    }
}
