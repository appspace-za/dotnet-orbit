using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <summary>
    /// The Not Planned sprint option (spec §6.3, §13 item 116): a task in no sprint is on the Backlog (InBacklog) or Not Planned.
    /// Every task already in no sprint stays on the Backlog, where it was; only new tasks start Not Planned. Down drops the flag,
    /// which puts every task in no sprint back on the Backlog.
    /// </summary>
    public partial class AddNotPlanned : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InBacklog",
                table: "Tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""UPDATE "Tasks" SET "InBacklog" = TRUE WHERE "SprintId" IS NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InBacklog",
                table: "Tasks");
        }
    }
}
