using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <summary>
    /// Parent tasks as containers (spec §6.15, §13 item 145; design spec 2026-11-02 §2, §3.4): HasSubtasks marks a task some task
    /// names as its parent, filled here from the existing subtasks. The startup step (ParentRollUp.RecalculateAllAsync) then
    /// rolls each parent up from its subtasks. Down drops the flag; the rolled-up values stay as they are.
    /// </summary>
    public partial class AddHasSubtasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasSubtasks",
                table: "Tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE \"Tasks\" t SET \"HasSubtasks\" = EXISTS (SELECT 1 FROM \"Tasks\" c WHERE c.\"ParentTaskId\" = t.\"Id\");");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_HasSubtasks",
                table: "Tasks",
                column: "HasSubtasks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tasks_HasSubtasks",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "HasSubtasks",
                table: "Tasks");
        }
    }
}
