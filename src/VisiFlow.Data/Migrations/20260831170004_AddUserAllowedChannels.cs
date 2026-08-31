using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VisiFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAllowedChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedChannels",
                table: "Users",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedChannels",
                table: "Users");
        }
    }
}
