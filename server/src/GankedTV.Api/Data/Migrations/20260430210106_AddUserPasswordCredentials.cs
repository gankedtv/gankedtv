using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPasswordCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "password_algo",
                table: "users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "password_hash",
                table: "users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            // Defence-in-depth: hash and algo must be set together. CredentialAuthService
            // already maintains the invariant by construction, but a manual UPDATE or a
            // future bug shouldn't be able to leave a row with a hash but no algo (or
            // vice versa) — that would corrupt verification and silently lock users out.
            migrationBuilder.AddCheckConstraint(
                name: "ck_users_password_hash_algo_paired",
                table: "users",
                sql: "(password_hash IS NULL AND password_algo IS NULL) "
                    + "OR (password_hash IS NOT NULL AND password_algo IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_password_hash_algo_paired",
                table: "users");

            migrationBuilder.DropColumn(
                name: "password_algo",
                table: "users");

            migrationBuilder.DropColumn(
                name: "password_hash",
                table: "users");
        }
    }
}
