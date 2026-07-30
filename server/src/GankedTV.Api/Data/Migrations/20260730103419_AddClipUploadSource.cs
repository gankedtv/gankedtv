using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClipUploadSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "upload_source",
                table: "clips",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "web");

            // Backfill: imported rows are recognizable by their preserved source URL. Pre-existing
            // API-key uploads can't be distinguished retroactively (the auth scheme wasn't
            // recorded), but none exist yet — rewynd's uploader ships after this migration.
            migrationBuilder.Sql("UPDATE clips SET upload_source = 'import' WHERE import_source_url IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "upload_source",
                table: "clips");
        }
    }
}
