using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Momos.Host.Migrations
{
    /// <inheritdoc />
    public partial class ModelKnowledgeIndexedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "KnowledgeIndexedAt",
                table: "ProjectModels",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KnowledgeIndexedAt",
                table: "ProjectModels");
        }
    }
}
