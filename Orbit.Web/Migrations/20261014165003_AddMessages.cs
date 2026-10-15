using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessageTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Condition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessageTemplateRecipients",
                columns: table => new
                {
                    MessageTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageTemplateRecipients", x => new { x.MessageTemplateId, x.Kind });
                    table.ForeignKey(
                        name: "FK_MessageTemplateRecipients_MessageTemplates_MessageTemplateId",
                        column: x => x.MessageTemplateId,
                        principalTable: "MessageTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutboundEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MessageTemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    MessageName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ToName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboundEmails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutboundEmails_AspNetUsers_ToUserId",
                        column: x => x.ToUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OutboundEmails_MessageTemplates_MessageTemplateId",
                        column: x => x.MessageTemplateId,
                        principalTable: "MessageTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageTemplates_Trigger",
                table: "MessageTemplates",
                column: "Trigger");

            migrationBuilder.CreateIndex(
                name: "IX_OutboundEmails_CreatedAt",
                table: "OutboundEmails",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OutboundEmails_MessageTemplateId",
                table: "OutboundEmails",
                column: "MessageTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboundEmails_Status_NextAttemptAt",
                table: "OutboundEmails",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboundEmails_ToUserId",
                table: "OutboundEmails",
                column: "ToUserId");

            // A message's name is unique ignoring case (spec §6.7). An expression index, which EF can't express; the service checks
            // the same rule first. messages.manage is granted to nobody by default, so there is no grant step.
            migrationBuilder.Sql("""CREATE UNIQUE INDEX "IX_MessageTemplates_Name_Lower" ON "MessageTemplates" (lower("Name"));""");

            // The messages Orbit starts with (spec §6.7): the five emails it sent before messages existed, in their words (1-7), and
            // the ones asked for with this feature (8-14). Literals, not the app's code, so the migration stays as it was written.
            migrationBuilder.Sql("""
                INSERT INTO "MessageTemplates" ("Id", "Name", "Trigger", "Condition", "Enabled", "Subject", "Body", "CreatedAt", "UpdatedAt") VALUES
                ('e9a5806b-3bba-408b-a48d-6e73767ad9a8', 'Task created: tell its assignees', 'TaskCreated', 'Assigned', true,
                 '[Orbit] Task assigned to you: {{task.title}}',
                 E'Hi {{recipient.name}},\n\n{{actor.name}} assigned you the task {{task.number}} {{task.title}}.\n{{task.url}}\n\nPriority: {{task.priority}} · Due: {{task.due}}', now(), now()),
                ('ca72c809-50f4-44e4-8151-bbd5f98cbe88', 'Task assigned: tell the people added', 'TaskAssigned', 'Always', true,
                 '[Orbit] Task assigned to you: {{task.title}}',
                 E'Hi {{recipient.name}},\n\n{{actor.name}} assigned you the task {{task.number}} {{task.title}}.\n{{task.url}}\n\nPriority: {{task.priority}} · Due: {{task.due}}', now(), now()),
                ('9497256c-8b1a-4a9e-b269-4cdbb87f7257', 'Task created: tell its requestee', 'TaskCreated', 'Always', true,
                 '[Orbit] Task created for you: {{task.title}}',
                 E'Hi {{recipient.name}},\n\n{{actor.name}} created the task {{task.number}} {{task.title}} for you. You can follow and update it as your own.\n{{task.url}}\n\nPriority: {{task.priority}} · Due: {{task.due}}', now(), now()),
                ('3dea4df9-c947-4d96-a9c8-f7a33a83634d', 'Requestee named: tell them', 'RequesteeNamed', 'Always', true,
                 '[Orbit] You are the requestee: {{task.title}}',
                 E'Hi {{recipient.name}},\n\n{{actor.name}} named you as the requestee of the task {{task.number}} {{task.title}}. You can follow and update it as your own.\n{{task.url}}\n\nPriority: {{task.priority}} · Due: {{task.due}}', now(), now()),
                ('1f7ccada-6679-4e8c-837d-d40a5bbe25d2', 'Task due soon: remind its assignees', 'TaskDueSoon', 'Always', true,
                 '[Orbit] Task due {{due.when}}: {{task.title}}',
                 E'Hi {{recipient.name}},\n\nThe task {{task.number}} {{task.title}} is due {{due.when}} ({{task.due}}).\n{{task.url}}\n\nStatus: {{task.status}} · Priority: {{task.priority}}', now(), now()),
                ('7adaa9ba-c3e0-4fbc-a60a-7f4e12a86deb', 'Request step: tell whoever it waits for', 'RequestStepWaiting', 'Always', true,
                 '[Orbit] {{request.number}}: please {{step.what}}',
                 E'Hi {{recipient.name}},\n\nThe request {{request.number}} {{request.title}}, logged by {{request.requester}}, needs you to {{step.what}}.\n{{request.url}}', now(), now()),
                ('e996c72a-6521-4865-affe-b14108723d15', 'Request finished: tell the requester', 'RequestFinished', 'Always', true,
                 '[Orbit] Request {{request.outcome}}: {{request.title}}',
                 E'Hi {{recipient.name}},\n\nYour request {{request.number}} {{request.title}} is {{request.outcome}}.\n{{request.url}}', now(), now()),
                ('63269713-c2c9-4bac-a865-a80ff3a9950c', 'Unassigned task: tell the department', 'TaskCreated', 'Unassigned', true,
                 '[Orbit] New unassigned task in {{task.department}}: {{task.title}}',
                 E'Hi {{recipient.name}},\n\n{{actor.name}} created the task {{task.number}} {{task.title}} in {{task.department}}. Nobody is assigned to it yet.\n{{task.url}}\n\nPriority: {{task.priority}} · Due: {{task.due}}\n\n{{task.description}}', now(), now()),
                ('3743c22b-9e82-456b-b00a-f8a1a347d04b', 'Task closed: tell its requestee', 'TaskClosed', 'Always', true,
                 '[Orbit] Task closed ({{task.status}}): {{task.title}}',
                 E'Hi {{recipient.name}},\n\n{{actor.name}} closed the task {{task.number}} {{task.title}}, which was created for you. Its status is now {{task.status}}.\n{{task.url}}', now(), now()),
                ('d438c1d0-30bc-4e90-b9ff-c6a2a9fdc050', 'Assignee comment: tell the requestee', 'TaskCommented', 'ByAssignee', true,
                 '[Orbit] New comment on {{task.title}}',
                 E'Hi {{recipient.name}},\n\n{{comment.author}} commented on the task {{task.number}} {{task.title}}, which was created for you:\n\n{{comment.body}}\n\n{{task.url}}', now(), now()),
                ('9c55a7a9-92f4-4885-bc76-d642dcbff6e9', 'Requestee comment: tell the assignees', 'TaskCommented', 'ByRequestee', true,
                 '[Orbit] New comment on {{task.title}}',
                 E'Hi {{recipient.name}},\n\n{{comment.author}}, whom the task is for, commented on the task {{task.number}} {{task.title}}:\n\n{{comment.body}}\n\n{{task.url}}', now(), now()),
                ('3443895c-7df9-43df-80fd-1bdb1c10025a', 'Approval given: tell the requester', 'ApprovalDecided', 'Approved', true,
                 '[Orbit] {{request.number}}: {{step.title}} approved',
                 E'Hi {{recipient.name}},\n\n{{approval.by}} approved "{{step.title}}" on your request {{request.number}} {{request.title}}.\n\n{{approval.comment}}\n\n{{request.url}}', now(), now()),
                ('cfa2a01f-6f8b-4b84-9fbf-a99ac882a7c8', 'Approval declined: tell the requester', 'ApprovalDecided', 'Declined', true,
                 '[Orbit] {{request.number}}: {{step.title}} declined',
                 E'Hi {{recipient.name}},\n\n{{approval.by}} declined "{{step.title}}" on your request {{request.number}} {{request.title}}.\n\n{{approval.comment}}\n\n{{request.url}}', now(), now()),
                ('3b32eedb-586a-463a-9561-8586c818146c', 'Action ran: tell the requester', 'ActionRan', 'Always', true,
                 '[Orbit] {{request.number}}: {{action.name}} {{action.outcome}}',
                 E'Hi {{recipient.name}},\n\nThe action "{{action.name}}" ran for your request {{request.number}} {{request.title}} and {{action.outcome}}.\n{{request.url}}', now(), now());

                INSERT INTO "MessageTemplateRecipients" ("MessageTemplateId", "Kind") VALUES
                ('e9a5806b-3bba-408b-a48d-6e73767ad9a8', 'Assignees'),
                ('ca72c809-50f4-44e4-8151-bbd5f98cbe88', 'AddedAssignees'),
                ('9497256c-8b1a-4a9e-b269-4cdbb87f7257', 'Requestee'),
                ('3dea4df9-c947-4d96-a9c8-f7a33a83634d', 'Requestee'),
                ('1f7ccada-6679-4e8c-837d-d40a5bbe25d2', 'Assignees'),
                ('7adaa9ba-c3e0-4fbc-a60a-7f4e12a86deb', 'StepPerson'),
                ('e996c72a-6521-4865-affe-b14108723d15', 'Requester'),
                ('63269713-c2c9-4bac-a865-a80ff3a9950c', 'DepartmentMembers'),
                ('3743c22b-9e82-456b-b00a-f8a1a347d04b', 'Requestee'),
                ('d438c1d0-30bc-4e90-b9ff-c6a2a9fdc050', 'Requestee'),
                ('9c55a7a9-92f4-4885-bc76-d642dcbff6e9', 'Assignees'),
                ('3443895c-7df9-43df-80fd-1bdb1c10025a', 'Requester'),
                ('cfa2a01f-6f8b-4b84-9fbf-a99ac882a7c8', 'Requester'),
                ('3b32eedb-586a-463a-9561-8586c818146c', 'Requester');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous build has no such permission. The messages, and the outbox with any email not yet sent, go with their tables.
            migrationBuilder.Sql("""DELETE FROM "RolePermissions" WHERE "Permission" = 'messages.manage';""");

            migrationBuilder.DropTable(
                name: "MessageTemplateRecipients");

            migrationBuilder.DropTable(
                name: "OutboundEmails");

            migrationBuilder.DropTable(
                name: "MessageTemplates");
        }
    }
}
