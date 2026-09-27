using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Momos.Host.Migrations
{
    /// <inheritdoc />
    public partial class ModelManualElements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Coverage",
                table: "ProjectModels",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Flows",
                table: "ProjectModels",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "Invariants",
                table: "ProjectModels",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "Outline",
                table: "ProjectModels",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Coverage",
                table: "ProjectModels");

            migrationBuilder.DropColumn(
                name: "Flows",
                table: "ProjectModels");

            migrationBuilder.DropColumn(
                name: "Invariants",
                table: "ProjectModels");

            migrationBuilder.DropColumn(
                name: "Outline",
                table: "ProjectModels");
        }
    }
}
