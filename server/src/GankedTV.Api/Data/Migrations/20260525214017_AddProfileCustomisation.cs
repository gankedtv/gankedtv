using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileCustomisation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "accent_color",
                table: "users",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "avatar_object_key",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "avatar_source",
                table: "users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "banner_object_key",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "banner_url",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "oauth_avatar_source",
                table: "users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "oauth_avatar_url",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "social_links",
                table: "users",
                type: "jsonb",
                nullable: true);

            // NOTE: the EF migration generator also produced AddColumn calls for
            // clips.failure_reason and clips.import_source_url here because earlier
            // migrations' Designer snapshots drifted out of sync with the model. Those
            // columns are already in the DB (added by their respective prior migrations) —
            // re-adding them would fail with 42701. Removed by hand to keep this migration
            // surgical to profile customisation only.

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_accent_color",
                table: "users",
                sql: "accent_color IS NULL OR accent_color ~ '^#[0-9A-Fa-f]{6}$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_accent_color",
                table: "users");

            migrationBuilder.DropColumn(
                name: "accent_color",
                table: "users");

            migrationBuilder.DropColumn(
                name: "avatar_object_key",
                table: "users");

            migrationBuilder.DropColumn(
                name: "avatar_source",
                table: "users");

            migrationBuilder.DropColumn(
                name: "banner_object_key",
                table: "users");

            migrationBuilder.DropColumn(
                name: "banner_url",
                table: "users");

            migrationBuilder.DropColumn(
                name: "oauth_avatar_source",
                table: "users");

            migrationBuilder.DropColumn(
                name: "oauth_avatar_url",
                table: "users");

            migrationBuilder.DropColumn(
                name: "social_links",
                table: "users");
        }
    }
}
