using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReportOtherNoteCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_reports_other_note",
                table: "reports",
                sql: "reason <> 'other' OR (note IS NOT NULL AND LENGTH(TRIM(note)) > 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_reports_other_note",
                table: "reports");
        }
    }
}
