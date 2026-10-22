using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestFlowEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A flow can be switched on and off (spec §6.20, §13 item 92). A new flow starts off, but the flows that exist now are
            // switched on, so nothing offered today disappears.
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                table: "RequestFlows",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""UPDATE "RequestFlows" SET "IsEnabled" = TRUE;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Without the switch a flow that is set up is offered, so a flow switched off is archived instead: going back never puts
            // a flow live that someone switched off. Going forward again leaves it archived, to restore by hand.
            migrationBuilder.Sql("""UPDATE "RequestFlows" SET "IsArchived" = TRUE WHERE NOT "IsEnabled";""");

            migrationBuilder.DropColumn(
                name: "IsEnabled",
                table: "RequestFlows");
        }
    }
}
