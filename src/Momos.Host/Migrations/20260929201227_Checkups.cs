using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Momos.Host.Migrations
{
    /// <inheritdoc />
    public partial class Checkups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReportLanguage",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "InspectionRequests",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Checkups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Language = table.Column<string>(type: "text", nullable: false),
                    CommitRef = table.Column<string>(type: "text", nullable: true),
                    BaseCommit = table.Column<string>(type: "text", nullable: true),
                    ModelVersion = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Checkups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Checkups_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExamRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Program = table.Column<string>(type: "text", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    ProjectModelId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExamRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExamRuns_Checkups_CheckupId",
                        column: x => x.CheckupId,
                        principalTable: "Checkups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExamRuns_InspectionRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "InspectionRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExamRuns_ProjectModels_ProjectModelId",
                        column: x => x.ProjectModelId,
                        principalTable: "ProjectModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Checkups_ProjectId_CreatedAt",
                table: "Checkups",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExamRuns_CheckupId",
                table: "ExamRuns",
                column: "CheckupId");

            migrationBuilder.CreateIndex(
                name: "IX_ExamRuns_ProjectModelId",
                table: "ExamRuns",
                column: "ProjectModelId");

            migrationBuilder.CreateIndex(
                name: "IX_ExamRuns_RequestId",
                table: "ExamRuns",
                column: "RequestId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExamRuns");

            migrationBuilder.DropTable(
                name: "Checkups");

            migrationBuilder.DropColumn(
                name: "ReportLanguage",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "InspectionRequests");
        }
    }
}
