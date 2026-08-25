using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClipPreEditSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "pre_edit_duration_secs",
                table: "clips",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "pre_edit_height",
                table: "clips",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "pre_edit_width",
                table: "clips",
                type: "smallint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pre_edit_duration_secs",
                table: "clips");

            migrationBuilder.DropColumn(
                name: "pre_edit_height",
                table: "clips");

            migrationBuilder.DropColumn(
                name: "pre_edit_width",
                table: "clips");
        }
    }
}
