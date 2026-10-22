using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddUrgencyMeanings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // An Urgency question can word its own levels (spec §6.20, §13 item 93). Every question there now gets none, so each
            // Urgency keeps the standard wording.
            migrationBuilder.AddColumn<List<string>>(
                name: "UrgencyMeanings",
                table: "RequestFormFields",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous build shows the standard wording and never reads the column, so it goes with nothing to convert.
            migrationBuilder.DropColumn(
                name: "UrgencyMeanings",
                table: "RequestFormFields");
        }
    }
}
