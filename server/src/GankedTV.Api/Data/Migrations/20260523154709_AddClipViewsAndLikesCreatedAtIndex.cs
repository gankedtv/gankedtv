using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClipViewsAndLikesCreatedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "clip_views",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    clip_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clip_views", x => x.id);
                    table.ForeignKey(
                        name: "fk_clip_views_clips_clip_id",
                        column: x => x.clip_id,
                        principalTable: "clips",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_likes_created_at_clip_id",
                table: "likes",
                columns: new[] { "created_at", "clip_id" },
                descending: new[] { true, false });

            migrationBuilder.CreateIndex(
                name: "idx_clip_views_clip_id_created_at",
                table: "clip_views",
                columns: new[] { "clip_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "idx_clip_views_created_at",
                table: "clip_views",
                column: "created_at",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clip_views");

            migrationBuilder.DropIndex(
                name: "idx_likes_created_at_clip_id",
                table: "likes");
        }
    }
}
