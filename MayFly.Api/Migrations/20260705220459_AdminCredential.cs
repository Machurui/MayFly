using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MayFly.Api.Migrations
{
    /// <inheritdoc />
    public partial class AdminCredential : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdminPasswordEnc",
                table: "Instances",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdminPasswordEnc",
                table: "Instances");
        }
    }
}
