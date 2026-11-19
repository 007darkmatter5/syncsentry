using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncSentry.Data.Migrations
{
    /// <inheritdoc />
    public partial class SetupWizard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SetupState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Finished = table.Column<bool>(type: "INTEGER", nullable: false),
                    LegacyUpgrade = table.Column<bool>(type: "INTEGER", nullable: false),
                    RcloneSkipped = table.Column<bool>(type: "INTEGER", nullable: false),
                    HelperSkipped = table.Column<bool>(type: "INTEGER", nullable: false),
                    AppdataHostPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SetupState", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SetupState");
        }
    }
}
