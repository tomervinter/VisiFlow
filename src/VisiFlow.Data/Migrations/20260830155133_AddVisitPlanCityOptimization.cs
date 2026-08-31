using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VisiFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitPlanCityOptimization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CityOptimizedAt",
                table: "VisitPlanEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CityOptimizedNote",
                table: "VisitPlanEntries",
                type: "TEXT",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CityOptimizedAt",
                table: "VisitPlanEntries");

            migrationBuilder.DropColumn(
                name: "CityOptimizedNote",
                table: "VisitPlanEntries");
        }
    }
}
