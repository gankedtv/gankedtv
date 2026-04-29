using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshTokenFamilyId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add the column with a per-row random uuid default so existing rows each become
            // their own single-member family. A blanket Guid.Empty would group every legacy
            // token into one family, letting a single replay revoke them all — exactly the
            // damage radius family revocation is supposed to bound.
            migrationBuilder.AddColumn<Guid>(
                name: "family_id",
                table: "refresh_tokens",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            // Drop the default so future inserts must supply FamilyId explicitly (RefreshTokenService
            // sets it on Issue and copies it on Rotate). Keeps the source-of-truth in C#.
            migrationBuilder.AlterColumn<Guid>(
                name: "family_id",
                table: "refresh_tokens",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.CreateIndex(
                name: "idx_refresh_tokens_family_id",
                table: "refresh_tokens",
                column: "family_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_refresh_tokens_family_id",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "family_id",
                table: "refresh_tokens");
        }
    }
}
