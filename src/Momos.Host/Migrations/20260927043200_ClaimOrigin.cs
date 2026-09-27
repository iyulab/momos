using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Momos.Host.Migrations
{
    /// <inheritdoc />
    public partial class ClaimOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "ModelClaims",
                type: "text",
                nullable: false,
                defaultValue: "Deterministic");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Origin",
                table: "ModelClaims");
        }
    }
}
