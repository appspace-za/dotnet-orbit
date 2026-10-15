using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskSolutionNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // How a task was resolved (spec §6.2.4). Tasks already Done keep none: the rule asks for notes only on the way to Done.
            migrationBuilder.AddColumn<string>(
                name: "SolutionNotes",
                table: "Tasks",
                type: "text",
                nullable: true);

            // The shipped "Task closed: tell its requestee" message (§6.7) tells them how the task was resolved - only while it is
            // still the body Orbit shipped, so an admin's own wording is left alone. Literals, so the migration stays as written.
            migrationBuilder.Sql("""
                UPDATE "MessageTemplates"
                SET "Body" = E'Hi {{recipient.name}},\n\n{{actor.name}} closed the task {{task.number}} {{task.title}}, which was created for you. Its status is now {{task.status}}.\n{{task.url}}\n\nSolution notes: {{task.solution}}',
                    "UpdatedAt" = now()
                WHERE "Id" = '3743c22b-9e82-456b-b00a-f8a1a347d04b'
                  AND "Body" = E'Hi {{recipient.name}},\n\n{{actor.name}} closed the task {{task.number}} {{task.title}}, which was created for you. Its status is now {{task.status}}.\n{{task.url}}';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The shipped body goes back to what it was, if it is still the one Up wrote. Any other message an admin wrote with
            // {{task.solution}} keeps it: the previous build renders the token as nothing, and asks for it to go when the message is
            // next saved. The notes themselves go with the column.
            migrationBuilder.Sql("""
                UPDATE "MessageTemplates"
                SET "Body" = E'Hi {{recipient.name}},\n\n{{actor.name}} closed the task {{task.number}} {{task.title}}, which was created for you. Its status is now {{task.status}}.\n{{task.url}}',
                    "UpdatedAt" = now()
                WHERE "Id" = '3743c22b-9e82-456b-b00a-f8a1a347d04b'
                  AND "Body" = E'Hi {{recipient.name}},\n\n{{actor.name}} closed the task {{task.number}} {{task.title}}, which was created for you. Its status is now {{task.status}}.\n{{task.url}}\n\nSolution notes: {{task.solution}}';
                """);

            migrationBuilder.DropColumn(
                name: "SolutionNotes",
                table: "Tasks");
        }
    }
}
