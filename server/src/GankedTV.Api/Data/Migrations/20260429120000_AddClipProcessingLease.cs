using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GankedTV.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClipProcessingLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "processing_started_at",
                table: "clips",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "processing_attempts",
                table: "clips",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "idx_clips_processing_updated_at",
                table: "clips",
                columns: new[] { "status", "updated_at" },
                filter: "status = 'processing'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_clips_processing_updated_at",
                table: "clips");

            migrationBuilder.DropColumn(
                name: "processing_attempts",
                table: "clips");

            migrationBuilder.DropColumn(
                name: "processing_started_at",
                table: "clips");
        }
    }
}
