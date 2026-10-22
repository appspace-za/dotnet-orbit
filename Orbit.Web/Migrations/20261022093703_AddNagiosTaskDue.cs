using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddNagiosTaskDue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // When a raised task is due (spec §6.21, §13 item 94). Every instance there now raises its tasks with no due date, as before.
            migrationBuilder.AddColumn<string>(
                name: "TaskDue",
                table: "NagiosInstances",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TaskDue",
                table: "NagiosInstances");
        }
    }
}
