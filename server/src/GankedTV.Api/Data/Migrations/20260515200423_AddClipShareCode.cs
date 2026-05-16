using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClipShareCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pgcrypto;");

            migrationBuilder.AddColumn<string>(
                name: "share_code",
                table: "clips",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true);

            // Backfill share_code using the same 62-char alphabet as ShareCodeGenerator.Next.
            // A pg_temp VOLATILE function forces fresh evaluation per row — a plain subquery is
            // collapsed to a single value by the planner. gen_random_bytes comes from pgcrypto.
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION pg_temp.gen_share_code() RETURNS text
                LANGUAGE sql VOLATILE AS $$
                    SELECT string_agg(
                        substr(
                            'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789',
                            (get_byte(gen_random_bytes(1), 0) % 62) + 1,
                            1
                        ),
                        '' ORDER BY i
                    )
                    FROM generate_series(1, 8) AS i;
                $$;

                UPDATE clips
                SET share_code = pg_temp.gen_share_code()
                WHERE share_code IS NULL;
            ");

            migrationBuilder.AlterColumn<string>(
                name: "share_code",
                table: "clips",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(12)",
                oldMaxLength: 12,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_clips_share_code",
                table: "clips",
                column: "share_code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_clips_share_code",
                table: "clips");

            migrationBuilder.DropColumn(
                name: "share_code",
                table: "clips");
        }
    }
}
