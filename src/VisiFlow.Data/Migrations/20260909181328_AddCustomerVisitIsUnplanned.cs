using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VisiFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerVisitIsUnplanned : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsUnplanned",
                table: "CustomerVisits",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsUnplanned",
                table: "CustomerVisits");
        }
    }
}
