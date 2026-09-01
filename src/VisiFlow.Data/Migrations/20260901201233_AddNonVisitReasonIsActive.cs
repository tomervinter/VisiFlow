using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VisiFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNonVisitReasonIsActive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "NonVisitReasons",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "NonVisitReasons");
        }
    }
}
