using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncSentry.Data.Migrations
{
    /// <inheritdoc />
    public partial class PipelineSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LidarrApiKeyProtected",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LidarrUrl",
                table: "Settings",
                type: "TEXT",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PlexTokenProtected",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PlexUrl",
                table: "Settings",
                type: "TEXT",
                maxLength: 300,
                nullable: false,
                defaultValue: "http://192.168.1.16:32400");

            migrationBuilder.AddColumn<string>(
                name: "RadarrApiKeyProtected",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RadarrUrl",
                table: "Settings",
                type: "TEXT",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SonarrApiKeyProtected",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SonarrUrl",
                table: "Settings",
                type: "TEXT",
                maxLength: 300,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LidarrApiKeyProtected",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "LidarrUrl",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PlexTokenProtected",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PlexUrl",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "RadarrApiKeyProtected",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "RadarrUrl",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SonarrApiKeyProtected",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SonarrUrl",
                table: "Settings");
        }
    }
}
