using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFailedClipIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Sibling of idx_clips_processing_updated_at for the maintenance failed-clip sweep
            // (status = 'failed' ORDER BY updated_at). Raw SQL rather than the EF model: EF keys
            // indexes by column set, so a second modeled index on { status, updated_at } would
            // overwrite the 'processing' one. Kept in sync manually with GankedTvDbContext.
            migrationBuilder.Sql(
                "CREATE INDEX idx_clips_failed_updated_at ON clips (status, updated_at) "
                + "WHERE status = 'failed';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS idx_clips_failed_updated_at;");
        }
    }
}
