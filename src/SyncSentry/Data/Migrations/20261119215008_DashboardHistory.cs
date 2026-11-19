using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncSentry.Data.Migrations
{
    /// <inheritdoc />
    public partial class DashboardHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "BandwidthMegabytes",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<string>(
                name: "BandwidthMode",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "off");

            migrationBuilder.AddColumn<int>(
                name: "HistoryDays",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleEndHour",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 23);

            migrationBuilder.AddColumn<double>(
                name: "ScheduleMegabytes",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleStartHour",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.CreateTable(
                name: "TransferEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DedupeKey = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Ok = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    DestinationPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransferEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    DarkMode = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransferEvents_CompletedUtc",
                table: "TransferEvents",
                column: "CompletedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TransferEvents_DedupeKey",
                table: "TransferEvents",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserPreferences_UserId",
                table: "UserPreferences",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TransferEvents");

            migrationBuilder.DropTable(
                name: "UserPreferences");

            migrationBuilder.DropColumn(
                name: "BandwidthMegabytes",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "BandwidthMode",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "HistoryDays",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ScheduleEndHour",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ScheduleMegabytes",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ScheduleStartHour",
                table: "Settings");
        }
    }
}
