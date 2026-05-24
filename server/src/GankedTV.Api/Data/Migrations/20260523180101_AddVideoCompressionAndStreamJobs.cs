using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoCompressionAndStreamJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "video_codec",
                table: "clips",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "clip_stream_jobs",
                columns: table => new
                {
                    clip_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    processing_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processing_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clip_stream_jobs", x => x.clip_id);
                    table.ForeignKey(
                        name: "fk_clip_stream_jobs_clips_clip_id",
                        column: x => x.clip_id,
                        principalTable: "clips",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_clip_stream_jobs_pending_updated_at",
                table: "clip_stream_jobs",
                columns: new[] { "status", "updated_at" },
                filter: "status = 'pending'");

            // Sibling of idx_clips_processing_updated_at for the compress stage's claim query
            // (status = 'transcoding' ORDER BY updated_at). Raw SQL rather than the EF model:
            // EF keys indexes by column set, so a second modeled index on { status, updated_at }
            // would overwrite the 'processing' one. Kept in sync manually with GankedTvDbContext.
            migrationBuilder.Sql(
                "CREATE INDEX idx_clips_transcoding_updated_at ON clips (status, updated_at) "
                + "WHERE status = 'transcoding';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS idx_clips_transcoding_updated_at;");

            migrationBuilder.DropTable(
                name: "clip_stream_jobs");

            migrationBuilder.DropColumn(
                name: "video_codec",
                table: "clips");
        }
    }
}
