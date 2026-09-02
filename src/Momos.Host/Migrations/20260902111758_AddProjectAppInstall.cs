using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Momos.Host.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectAppInstall : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppInstallArgs",
                table: "Projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppInstallLaunchCommand",
                table: "Projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppInstallPlatform",
                table: "Projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppInstallerUri",
                table: "Projects",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppInstallArgs",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AppInstallLaunchCommand",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AppInstallPlatform",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AppInstallerUri",
                table: "Projects");
        }
    }
}
