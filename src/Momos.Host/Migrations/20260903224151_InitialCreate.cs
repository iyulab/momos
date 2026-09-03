using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Momos.Host.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(36)", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    RepositoryUrl = table.Column<string>(type: "text", nullable: true),
                    DeploymentUrl = table.Column<string>(type: "text", nullable: true),
                    AppInstallerUri = table.Column<string>(type: "text", nullable: true),
                    AppInstallPlatform = table.Column<string>(type: "text", nullable: true),
                    AppInstallArgs = table.Column<string>(type: "text", nullable: true),
                    AppInstallLaunchCommand = table.Column<string>(type: "text", nullable: true),
                    Purpose = table.Column<string>(type: "text", nullable: false),
                    Vision = table.Column<string>(type: "text", nullable: false),
                    Scope = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InspectionRequests",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(36)", nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(36)", nullable: false),
                    Focus = table.Column<string>(type: "text", nullable: true),
                    CommitRef = table.Column<string>(type: "text", nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InspectionRequests_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InspectionReports",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(36)", nullable: false),
                    InspectionRequestId = table.Column<string>(type: "character varying(36)", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InspectionReports_InspectionRequests_InspectionRequestId",
                        column: x => x.InspectionRequestId,
                        principalTable: "InspectionRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Findings",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(36)", nullable: false),
                    InspectionReportId = table.Column<string>(type: "character varying(36)", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Findings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Findings_InspectionReports_InspectionReportId",
                        column: x => x.InspectionReportId,
                        principalTable: "InspectionReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ToolCalls",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(36)", nullable: false),
                    InspectionReportId = table.Column<string>(type: "character varying(36)", nullable: false),
                    Tool = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolCalls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ToolCalls_InspectionReports_InspectionReportId",
                        column: x => x.InspectionReportId,
                        principalTable: "InspectionReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Findings_InspectionReportId",
                table: "Findings",
                column: "InspectionReportId");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionReports_InspectionRequestId",
                table: "InspectionReports",
                column: "InspectionRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InspectionRequests_ProjectId",
                table: "InspectionRequests",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ToolCalls_InspectionReportId",
                table: "ToolCalls",
                column: "InspectionReportId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Findings");

            migrationBuilder.DropTable(
                name: "ToolCalls");

            migrationBuilder.DropTable(
                name: "InspectionReports");

            migrationBuilder.DropTable(
                name: "InspectionRequests");

            migrationBuilder.DropTable(
                name: "Projects");
        }
    }
}
