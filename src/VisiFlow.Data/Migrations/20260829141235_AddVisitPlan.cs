using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VisiFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VisitPlanEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlanYear = table.Column<int>(type: "INTEGER", nullable: false),
                    PlanMonth = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerNumber = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    PlannedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AgentName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PriorityScore = table.Column<decimal>(type: "TEXT", nullable: false),
                    SalesDropScore = table.Column<decimal>(type: "TEXT", nullable: false),
                    DistributionScore = table.Column<decimal>(type: "TEXT", nullable: false),
                    FrequencyScore = table.Column<decimal>(type: "TEXT", nullable: false),
                    VisitStandardScore = table.Column<decimal>(type: "TEXT", nullable: false),
                    DaysSinceVisitScore = table.Column<decimal>(type: "TEXT", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisitPlanEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VisitPlanEntries_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VisitPlanWeights",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    SalesDropWeight = table.Column<decimal>(type: "TEXT", nullable: false),
                    DistributionWeight = table.Column<decimal>(type: "TEXT", nullable: false),
                    FrequencyWeight = table.Column<decimal>(type: "TEXT", nullable: false),
                    VisitStandardWeight = table.Column<decimal>(type: "TEXT", nullable: false),
                    DaysSinceVisitWeight = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisitPlanWeights", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VisitPlanWeights_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VisitPlanEntries_CompanyId_PlanYear_PlanMonth",
                table: "VisitPlanEntries",
                columns: new[] { "CompanyId", "PlanYear", "PlanMonth" });

            migrationBuilder.CreateIndex(
                name: "IX_VisitPlanWeights_CompanyId",
                table: "VisitPlanWeights",
                column: "CompanyId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VisitPlanEntries");

            migrationBuilder.DropTable(
                name: "VisitPlanWeights");
        }
    }
}
