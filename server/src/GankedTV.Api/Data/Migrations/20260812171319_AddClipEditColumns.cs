using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClipEditColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "edit_count",
                table: "clips",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "edited_at",
                table: "clips",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "edit_count",
                table: "clips");

            migrationBuilder.DropColumn(
                name: "edited_at",
                table: "clips");
        }
    }
}
