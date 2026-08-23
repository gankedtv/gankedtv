using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MergeDuplicateGamesAndUniqueIgdbId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Links the curated "Overwatch 2" seed to IGDB 125174. On a database that already
            // grew the duplicate row (the importer minted one after IGDB renamed 125174 back to
            // "Overwatch"), this momentarily leaves two rows claiming the id — the merge below
            // collapses them before the unique index is built.
            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 7,
                column: "igdb_id",
                value: 125174);

            // Collapse every duplicate igdb_id group, repointing clips onto the survivor.
            // Curated rows win over importer-managed ones — they carry the hand-picked slug and
            // tag that existing links point at — and within a group the lowest (oldest) id wins.
            // Written against the data rather than specific row ids because the duplicate's id
            // differs per environment; a no-op on a database that never grew one.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    grp record;
                    keep_id integer;
                BEGIN
                    FOR grp IN
                        SELECT igdb_id FROM games
                        WHERE igdb_id IS NOT NULL
                        GROUP BY igdb_id HAVING count(*) > 1
                    LOOP
                        SELECT id INTO keep_id FROM games
                        WHERE igdb_id = grp.igdb_id
                        ORDER BY igdb_managed ASC, id ASC
                        LIMIT 1;

                        UPDATE clips SET game_id = keep_id
                        WHERE game_id IN (
                            SELECT id FROM games WHERE igdb_id = grp.igdb_id AND id <> keep_id);

                        DELETE FROM games WHERE igdb_id = grp.igdb_id AND id <> keep_id;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "idx_games_igdb_id",
                table: "games",
                column: "igdb_id",
                unique: true,
                filter: "igdb_id IS NOT NULL");
        }

        /// <inheritdoc />
        // The merge is one-way: the duplicate rows and the clip assignments they held are gone.
        // Down only unwinds the schema and the seed link.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_games_igdb_id",
                table: "games");

            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 7,
                column: "igdb_id",
                value: null);
        }
    }
}
