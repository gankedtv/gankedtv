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
            // Links the curated "Overwatch 2" seed to IGDB 125174. Where the duplicate row
            // already exists this leaves two rows claiming the id; the merge below collapses
            // them before the unique index is built.
            migrationBuilder.UpdateData(
                table: "games",
                keyColumn: "id",
                keyValue: 7,
                column: "igdb_id",
                value: 125174);

            // Collapse duplicate igdb_id groups, repointing clips onto the survivor: curated
            // rows win (they carry the slug existing links use), then lowest id. Keyed on the
            // data, not row ids — the duplicate's id differs per environment.
            //
            // The survivor inherits the duplicate's cover art first: the mirrored cover lives on
            // the importer-managed row, and the curated seed's columns are NULL in prod, so
            // deleting without this leaves the game coverless until someone runs the importer
            // (IgdbSyncHostedService is off by default). The duplicate's blob in the game-covers
            // bucket is left behind — harmless, and the survivor now points at it.
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

                        UPDATE games k
                        SET cover_image_id = d.cover_image_id, cover_url = d.cover_url
                        FROM (SELECT cover_image_id, cover_url FROM games
                              WHERE igdb_id = grp.igdb_id AND id <> keep_id
                                AND cover_image_id IS NOT NULL
                              LIMIT 1) d
                        WHERE k.id = keep_id AND k.cover_image_id IS NULL;

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
        // One-way: the merged rows and their clip assignments are gone.
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
