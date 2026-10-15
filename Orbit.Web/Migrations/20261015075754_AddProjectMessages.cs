using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The project triggers (spec §6.7) need no schema change: a trigger, condition and recipient are stored as their names.
            // The messages Orbit starts with for them, as literals so the migration stays as it was written.
            migrationBuilder.Sql("""
                INSERT INTO "MessageTemplates" ("Id", "Name", "Trigger", "Condition", "Enabled", "Subject", "Body", "CreatedAt", "UpdatedAt") VALUES
                ('5849542c-470d-4e8e-83bb-8436c81018da', 'Project created: tell its owner', 'ProjectCreated', 'Always', true,
                 '[Orbit] Project created for you: {{project.name}}',
                 E'Hi {{recipient.name}},\n\n{{actor.name}} created the project {{project.number}} {{project.name}} in {{project.department}}, with you as its owner.\n{{project.url}}\n\nTarget date: {{project.target}}', now(), now()),
                ('be4abe4c-aa38-4706-8ba1-f7792d51fa17', 'Project owner changed: tell the new owner', 'ProjectOwnerChanged', 'Always', true,
                 '[Orbit] You now own the project {{project.name}}',
                 E'Hi {{recipient.name}},\n\n{{actor.name}} made you the owner of the project {{project.number}} {{project.name}}, which {{project.previous-owner}} owned before.\n{{project.url}}\n\nStatus: {{project.status}} · Target date: {{project.target}}', now(), now()),
                ('34b905f5-1fbc-4f7f-9b04-f13736342eb8', 'Project comment: tell its owner', 'ProjectCommented', 'ByOther', true,
                 '[Orbit] New comment on the project {{project.name}}',
                 E'Hi {{recipient.name}},\n\n{{comment.author}} commented on your project {{project.number}} {{project.name}}:\n\n{{comment.body}}\n\n{{project.url}}', now(), now()),
                ('128ff113-b90d-4832-ac0d-20465516be5c', 'Project status changed: tell the department', 'ProjectStatusChanged', 'Always', true,
                 '[Orbit] Project {{project.name}} is now {{project.status}}',
                 E'Hi {{recipient.name}},\n\n{{actor.name}} changed the status of the project {{project.number}} {{project.name}} from {{project.previous-status}} to {{project.status}}.\n{{project.url}}', now(), now());

                INSERT INTO "MessageTemplateRecipients" ("MessageTemplateId", "Kind") VALUES
                ('5849542c-470d-4e8e-83bb-8436c81018da', 'ProjectOwner'),
                ('be4abe4c-aa38-4706-8ba1-f7792d51fa17', 'ProjectOwner'),
                ('34b905f5-1fbc-4f7f-9b04-f13736342eb8', 'ProjectOwner'),
                ('128ff113-b90d-4832-ac0d-20465516be5c', 'ProjectOwner'),
                ('128ff113-b90d-4832-ac0d-20465516be5c', 'DepartmentMembers');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous build throws on a trigger it doesn't know: every project message goes - the shipped ones and any an admin
            // added, their recipients with them - and so do the emails they wrote. Their conditions and the project's owner as a
            // recipient exist only on project triggers.
            migrationBuilder.Sql("""
                DELETE FROM "OutboundEmails" WHERE "Trigger" IN ('ProjectCreated', 'ProjectStatusChanged', 'ProjectOwnerChanged', 'ProjectCommented');
                DELETE FROM "MessageTemplates" WHERE "Trigger" IN ('ProjectCreated', 'ProjectStatusChanged', 'ProjectOwnerChanged', 'ProjectCommented');
                """);
        }
    }
}
