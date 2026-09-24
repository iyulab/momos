using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Momos.Host.Migrations
{
    /// <inheritdoc />
    public partial class ProjectModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelVersion = table.Column<int>(type: "integer", nullable: false),
                    BaseCommit = table.Column<string>(type: "text", nullable: false),
                    AnalysisRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Components = table.Column<string>(type: "jsonb", nullable: false),
                    Relations = table.Column<string>(type: "jsonb", nullable: false),
                    Patterns = table.Column<string>(type: "jsonb", nullable: false),
                    Decisions = table.Column<string>(type: "jsonb", nullable: false),
                    Intents = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectModels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectModels_InspectionRequests_AnalysisRequestId",
                        column: x => x.AnalysisRequestId,
                        principalTable: "InspectionRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectModels_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ModelClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Tier = table.Column<string>(type: "text", nullable: false),
                    Statement = table.Column<string>(type: "text", nullable: false),
                    Evidence = table.Column<string>(type: "jsonb", nullable: false),
                    Confidence = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Correction = table.Column<string>(type: "text", nullable: true),
                    CorrectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModelClaims_ProjectModels_ProjectModelId",
                        column: x => x.ProjectModelId,
                        principalTable: "ProjectModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModelClaims_ProjectModelId_Key",
                table: "ModelClaims",
                columns: new[] { "ProjectModelId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectModels_AnalysisRequestId",
                table: "ProjectModels",
                column: "AnalysisRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectModels_ProjectId_ModelVersion",
                table: "ProjectModels",
                columns: new[] { "ProjectId", "ModelVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModelClaims");

            migrationBuilder.DropTable(
                name: "ProjectModels");
        }
    }
}
