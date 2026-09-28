using Microsoft.EntityFrameworkCore.Migrations;

namespace SampleShop.Data.Migrations;

public partial class Init : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OrderLines",
            columns: table => new { });
    }
}
