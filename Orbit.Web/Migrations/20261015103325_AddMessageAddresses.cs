using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageAddresses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Addresses",
                table: "MessageTemplates",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The listed addresses go. A message that had only addresses is left with no recipient: the previous build sends it to
            // nobody, and asks for a recipient when it is next saved.
            migrationBuilder.DropColumn(
                name: "Addresses",
                table: "MessageTemplates");
        }
    }
}
