using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGameTagAndExtraGames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "tag",
                table: "games",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 1,
                column: "tag",
                value: "LOL");

            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 2,
                column: "tag",
                value: "VALORANT");

            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 3,
                column: "tag",
                value: "CS2");

            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 4,
                column: "tag",
                value: "FN");

            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 5,
                column: "tag",
                value: "APEX");

            migrationBuilder.InsertData(
                table: "games",
                columns: new[] { "id", "cover_url", "igdb_id", "name", "slug", "tag" },
                values: new object[,]
                {
                    { 6, null, null, "Rocket League", "rocket-league", "RL" },
                    { 7, null, null, "Overwatch 2", "overwatch-2", "OW2" },
                    { 8, null, null, "Dota 2", "dota-2", "DOTA2" },
                    { 9, null, null, "Marvel Rivals", "marvel-rivals", "RIVALS" }
                });

            migrationBuilder.CreateIndex(
                name: "idx_games_name",
                table: "games",
                column: "name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_games_name",
                table: "games");

            migrationBuilder.DeleteData(
                table: "games",
                keyColumn: "id",
                keyValue: 6);

            migrationBuilder.DeleteData(
                table: "games",
                keyColumn: "id",
                keyValue: 7);

            migrationBuilder.DeleteData(
                table: "games",
                keyColumn: "id",
                keyValue: 8);

            migrationBuilder.DeleteData(
                table: "games",
                keyColumn: "id",
                keyValue: 9);

            migrationBuilder.DropColumn(
                name: "tag",
                table: "games");
        }
    }
}
