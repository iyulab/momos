using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Momos.Host.Migrations
{
    /// <inheritdoc />
    public partial class AddInspectionRequestClaimedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimedAt",
                table: "InspectionRequests",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "InspectionRequests");
        }
    }
}
