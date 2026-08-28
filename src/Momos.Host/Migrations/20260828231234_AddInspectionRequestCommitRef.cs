using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Momos.Host.Migrations
{
    /// <inheritdoc />
    public partial class AddInspectionRequestCommitRef : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CommitRef",
                table: "InspectionRequests",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommitRef",
                table: "InspectionRequests");
        }
    }
}
