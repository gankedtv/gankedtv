using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWrongGameReportReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_reports_reason",
                table: "reports");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reports_reason",
                table: "reports",
                sql: "reason IN ('spam','harassment','hate','nsfw','violence','wrong_game','other')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_reports_reason",
                table: "reports");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reports_reason",
                table: "reports",
                sql: "reason IN ('spam','harassment','hate','nsfw','violence','other')");
        }
    }
}
