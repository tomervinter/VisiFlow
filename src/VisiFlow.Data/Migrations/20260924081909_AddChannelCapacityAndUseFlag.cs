using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VisiFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelCapacityAndUseFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UseChannelCapacity",
                table: "VisitPlanWeights",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ChannelCapacities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FullDayCapacity = table.Column<int>(type: "INTEGER", nullable: false),
                    HalfDayCapacity = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelCapacities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelCapacities_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelCapacities_CompanyId_Channel",
                table: "ChannelCapacities",
                columns: new[] { "CompanyId", "Channel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelCapacities");

            migrationBuilder.DropColumn(
                name: "UseChannelCapacity",
                table: "VisitPlanWeights");
        }
    }
}
