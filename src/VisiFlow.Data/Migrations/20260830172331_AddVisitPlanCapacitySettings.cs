using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VisiFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitPlanCapacitySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defaults match the values these were hardcoded to before this became configurable - an
            // existing company's row must NOT backfill to 0, which would make CapacityFor() return 0
            // for every working day and silently schedule nothing at all.
            migrationBuilder.AddColumn<int>(
                name: "FullDayCapacity",
                table: "VisitPlanWeights",
                type: "INTEGER",
                nullable: false,
                defaultValue: 8);

            migrationBuilder.AddColumn<int>(
                name: "HalfDayCapacity",
                table: "VisitPlanWeights",
                type: "INTEGER",
                nullable: false,
                defaultValue: 4);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FullDayCapacity",
                table: "VisitPlanWeights");

            migrationBuilder.DropColumn(
                name: "HalfDayCapacity",
                table: "VisitPlanWeights");
        }
    }
}
