using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchVectors : Migration
    {
        // Prod-rollout note: both `ALTER TABLE … ADD COLUMN … GENERATED ALWAYS AS … STORED`
        // and `CREATE INDEX … USING GIN` take ACCESS EXCLUSIVE locks and rewrite the table.
        // Safe at current scale; for a large populated `clips` table this should be staged:
        //   1. Add a nullable column (no GENERATED).
        //   2. Backfill in batches.
        //   3. CREATE INDEX CONCURRENTLY.
        //   4. Swap to a stored-generated column or replace the backfill with a trigger.
        // EF Core doesn't emit CONCURRENTLY, so the staged variant has to be hand-rolled.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "games",
                type: "tsvector",
                nullable: false,
                computedColumnSql: "to_tsvector('simple', coalesce(name, ''))",
                stored: true);

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "clips",
                type: "tsvector",
                nullable: false,
                computedColumnSql: "setweight(to_tsvector('simple', coalesce(title, '')), 'A') || setweight(to_tsvector('simple', coalesce(description, '')), 'B')",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "idx_games_search_vector",
                table: "games",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "idx_clips_search_vector",
                table: "clips",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_games_search_vector",
                table: "games");

            migrationBuilder.DropIndex(
                name: "idx_clips_search_vector",
                table: "clips");

            migrationBuilder.DropColumn(
                name: "search_vector",
                table: "games");

            migrationBuilder.DropColumn(
                name: "search_vector",
                table: "clips");
        }
    }
}
