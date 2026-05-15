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

            migrationBuilder.Sql(@"
                UPDATE clips
                SET share_code = substr(encode(digest(id::text, 'sha256'), 'hex'), 1, 8)
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
