using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTopRankedClipIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "idx_clips_top_ranked",
                table: "clips",
                columns: new[] { "like_count", "view_count", "created_at", "id" },
                descending: new bool[0],
                filter: "status = 'ready' AND visibility = 'public'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_clips_top_ranked",
                table: "clips");
        }
    }
}
