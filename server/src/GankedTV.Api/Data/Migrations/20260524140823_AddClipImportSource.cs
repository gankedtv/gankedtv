using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClipImportSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "import_source_url",
                table: "clips",
                type: "text",
                nullable: true);

            // Sibling of idx_clips_processing_updated_at / idx_clips_transcoding_updated_at
            // for the new ImportWorker's claim query (status = 'importing' ORDER BY updated_at).
            // Raw SQL rather than the EF model: EF keys indexes by column set, so a third
            // modeled index on { status, updated_at } would overwrite the 'processing' one.
            migrationBuilder.Sql(
                "CREATE INDEX idx_clips_importing_updated_at ON clips (status, updated_at) "
                + "WHERE status = 'importing';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS idx_clips_importing_updated_at;");

            migrationBuilder.DropColumn(
                name: "import_source_url",
                table: "clips");
        }
    }
}
