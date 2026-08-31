using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VisiFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitPlanEntryManualEdit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ManuallyModifiedAt",
                table: "VisitPlanEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManuallyModifiedNote",
                table: "VisitPlanEntries",
                type: "TEXT",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManuallyModifiedAt",
                table: "VisitPlanEntries");

            migrationBuilder.DropColumn(
                name: "ManuallyModifiedNote",
                table: "VisitPlanEntries");
        }
    }
}
