using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGameCoverImageIdAndIgdbManaged : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cover_image_id",
                table: "games",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "igdb_managed",
                table: "games",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 1,
                column: "cover_image_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 2,
                column: "cover_image_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 3,
                column: "cover_image_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 4,
                column: "cover_image_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 5,
                column: "cover_image_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 6,
                column: "cover_image_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 7,
                column: "cover_image_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 8,
                column: "cover_image_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 9,
                column: "cover_image_id",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cover_image_id",
                table: "games");

            migrationBuilder.DropColumn(
                name: "igdb_managed",
                table: "games");
        }
    }
}
