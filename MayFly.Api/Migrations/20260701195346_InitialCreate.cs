using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MayFly.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Instances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CapabilityToken = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "text", nullable: false),
                    CreatorIp = table.Column<string>(type: "text", nullable: false),
                    Engine = table.Column<string>(type: "text", nullable: false),
                    TtlHours = table.Column<int>(type: "integer", nullable: false),
                    StorageQuotaMb = table.Column<int>(type: "integer", nullable: false),
                    InitialData = table.Column<string>(type: "text", nullable: false),
                    ContainerId = table.Column<string>(type: "text", nullable: false),
                    VolumeName = table.Column<string>(type: "text", nullable: false),
                    InternalHost = table.Column<string>(type: "text", nullable: false),
                    PublicPort = table.Column<int>(type: "integer", nullable: false),
                    DbName = table.Column<string>(type: "text", nullable: false),
                    DbUser = table.Column<string>(type: "text", nullable: false),
                    DbPasswordEnc = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSizeBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QueryLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueryLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Instances_CapabilityToken",
                table: "Instances",
                column: "CapabilityToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Instances_CreatorIp_State",
                table: "Instances",
                columns: new[] { "CreatorIp", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Instances_SessionId",
                table: "Instances",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryLogs_InstanceId",
                table: "QueryLogs",
                column: "InstanceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Instances");

            migrationBuilder.DropTable(
                name: "QueryLogs");
        }
    }
}
