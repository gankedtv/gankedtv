using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClipTrimColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "trim_end_secs",
                table: "clips",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "trim_start_secs",
                table: "clips",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trim_end_secs",
                table: "clips");

            migrationBuilder.DropColumn(
                name: "trim_start_secs",
                table: "clips");
        }
    }
}
