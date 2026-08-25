using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClipCropColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "crop_height",
                table: "clips",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "crop_width",
                table: "clips",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "crop_x",
                table: "clips",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "crop_y",
                table: "clips",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_clips_crop_rect",
                table: "clips",
                sql: "(crop_x IS NULL AND crop_y IS NULL AND crop_width IS NULL AND crop_height IS NULL) OR (crop_x IS NOT NULL AND crop_y IS NOT NULL AND crop_width IS NOT NULL AND crop_height IS NOT NULL AND crop_x >= 0 AND crop_y >= 0 AND crop_width > 0 AND crop_height > 0 AND crop_x + crop_width <= 1 AND crop_y + crop_height <= 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_clips_crop_rect",
                table: "clips");

            migrationBuilder.DropColumn(
                name: "crop_height",
                table: "clips");

            migrationBuilder.DropColumn(
                name: "crop_width",
                table: "clips");

            migrationBuilder.DropColumn(
                name: "crop_x",
                table: "clips");

            migrationBuilder.DropColumn(
                name: "crop_y",
                table: "clips");
        }
    }
}
