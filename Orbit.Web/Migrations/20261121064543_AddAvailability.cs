using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UnavailableTimes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    To = table.Column<DateOnly>(type: "date", nullable: false),
                    MinutesPerDay = table.Column<int>(type: "integer", nullable: true),
                    Reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnavailableTimes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UnavailableTimes_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UnavailableTimes_AspNetUsers_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UnavailableTimes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkPatterns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    MondayMinutes = table.Column<int>(type: "integer", nullable: false),
                    TuesdayMinutes = table.Column<int>(type: "integer", nullable: false),
                    WednesdayMinutes = table.Column<int>(type: "integer", nullable: false),
                    ThursdayMinutes = table.Column<int>(type: "integer", nullable: false),
                    FridayMinutes = table.Column<int>(type: "integer", nullable: false),
                    SaturdayMinutes = table.Column<int>(type: "integer", nullable: false),
                    SundayMinutes = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkPatterns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkPatterns_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WorkPatterns_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UnavailableTimes_CreatedById",
                table: "UnavailableTimes",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_UnavailableTimes_UpdatedById",
                table: "UnavailableTimes",
                column: "UpdatedById");

            migrationBuilder.CreateIndex(
                name: "IX_UnavailableTimes_UserId_From",
                table: "UnavailableTimes",
                columns: new[] { "UserId", "From" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkPatterns_CreatedById",
                table: "WorkPatterns",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_WorkPatterns_UserId_EffectiveFrom",
                table: "WorkPatterns",
                columns: new[] { "UserId", "EffectiveFrom" },
                unique: true);

            // Everyone starts on the standard week (spec §6.22): the organisation's working days (Monday to Friday if the calendar
            // was never saved) at 8 hours, from the day their account was created. SQL can't read CriticalPath:HoursPerWorkingDay, so 8
            // is written in here; people created after this migration take it from configuration. System accounts get none.
            migrationBuilder.Sql($"""
                INSERT INTO "WorkPatterns" ("Id", "UserId", "EffectiveFrom", "MondayMinutes", "TuesdayMinutes", "WednesdayMinutes",
                    "ThursdayMinutes", "FridayMinutes", "SaturdayMinutes", "SundayMinutes", "CreatedAt", "CreatedById")
                SELECT gen_random_uuid(), u."Id", (u."CreatedAt" AT TIME ZONE 'UTC')::date,
                    CASE WHEN COALESCE(c."MondayWorking", TRUE) THEN 480 ELSE 0 END,
                    CASE WHEN COALESCE(c."TuesdayWorking", TRUE) THEN 480 ELSE 0 END,
                    CASE WHEN COALESCE(c."WednesdayWorking", TRUE) THEN 480 ELSE 0 END,
                    CASE WHEN COALESCE(c."ThursdayWorking", TRUE) THEN 480 ELSE 0 END,
                    CASE WHEN COALESCE(c."FridayWorking", TRUE) THEN 480 ELSE 0 END,
                    CASE WHEN COALESCE(c."SaturdayWorking", FALSE) THEN 480 ELSE 0 END,
                    CASE WHEN COALESCE(c."SundayWorking", FALSE) THEN 480 ELSE 0 END,
                    now(), NULL
                FROM "AspNetUsers" u
                LEFT JOIN "WorkingCalendars" c ON c."Id" = '{Orbit.Data.WellKnownIds.WorkingCalendarId}'
                WHERE NOT u."IsSystemAccount";
                """);

            // The shipped roles' default grant (spec §6.5), for a database whose roles already exist - a new database gets it from
            // DbInitializer when it creates the roles. A shipped role that has been renamed or deleted is left alone.
            migrationBuilder.Sql("""
                INSERT INTO "RolePermissions" ("RoleId", "Permission", "Scope")
                SELECT r."Id", g.permission, g.scope
                FROM "AspNetRoles" r
                JOIN (VALUES
                    ('Member', 'availability.manage', 'Own'),
                    ('Department Admin', 'availability.manage', 'Department')
                ) AS g(role, permission, scope) ON g.role = r."Name"
                WHERE NOT r."IsBuiltIn"
                ON CONFLICT ("RoleId", "Permission") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM "RolePermissions" WHERE "Permission" = 'availability.manage';""");

            migrationBuilder.DropTable(
                name: "UnavailableTimes");

            migrationBuilder.DropTable(
                name: "WorkPatterns");
        }
    }
}
