# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Orbit is a task/project tracker and asset register: ASP.NET Core on .NET 10 (Blazor Server + MudBlazor; Razor Pages
only for sign-in and the account pages, and the error page), EF Core + PostgreSQL,
ASP.NET Core Identity, an MCP server for Claude, Quartz.NET jobs and QuestPDF reports. `README.md` covers the
feature set, the permission table and configuration. `orbit-spec.md` is the full specification and the source of truth.

## Commands

```
dotnet build Orbit.slnx
dotnet run --project Orbit.Web                        # needs Orbit.Web/appsettings.Development.json (gitignored) with the Postgres connection string
dotnet test Orbit.slnx
dotnet test Orbit.Tests --filter "FullyQualifiedName~CriticalPathEngineTests"   # one class; use ~ClassName.MethodName for one test
dotnet tool restore                                   # once, pins dotnet-ef 10.0.12
dotnet ef migrations add <Name> --project Orbit.Web   # run from the repo root
dotnet run --project Orbit.Agent -- help              # the on-premises agent's CLI (configure / run / remove)
```

- Startup applies pending migrations automatically (`Database:ApplyMigrations`, default true) and seeds roles, the
  synthetic Claude user and the first admin (`DbInitializer`). No separate `database update` step is needed in dev.
- If an `Orbit.Web` process is running (e.g. from the IDE on port 5016/7179), it locks `bin/Debug` and `dotnet build`
  fails with MSB3027. Build and test with `-c Release` instead of killing it.
- `Orbit.Agent` reads and writes its credential in `agent.json` beside the executable. Never run `configure` in
  someone's `bin/` folder; set `ORBIT_AGENT_HOME` to a scratch directory for tests. The credential can't be recovered.
- Tests are xunit and cover pure code only (access rules, role rules, scoping, scheduling, asset rules). There are
  no database or integration tests. Verify DB-touching changes by running the app.

## Solution layout and namespaces

`Orbit.Web` is one project whose folders stand in for layers (`Data/`, `Application/`, `Auth/`, `Mcp/`, `Jobs/`,
`Agents/`, `Components/` for Blazor, `Pages/` for Razor Pages). `RootNamespace` is `Orbit`, so namespaces are
`Orbit.Application.Services`, `Orbit.Data.Entities`, `Orbit.Components.Tasks` and so on, **not** `Orbit.Web.*`. Match this in new files. `Orbit.Agents.Contracts` is deliberately plural: a
namespace `Orbit.Agent` visible to the web app would shadow the `Agent` entity.

## How a request is handled

Blazor components, Razor Pages and MCP tools are thin. All call the same scoped services in
`Orbit.Web/Application/Services/`, and **all authorization lives in the services**:

- Each service gets the caller from `IActorProvider.GetAsync()`, which returns an `Actor` (user, API key or system)
  holding a role's permissions, each granted at a `PermissionScope` (None < Own < Department < All). `HttpActorProvider`
  re-reads grants from the DB on every request. Background jobs pass `Actor.System` explicitly.
- Object checks go through `AccessPolicy` (`AccessPolicy.Require(AccessPolicy.CanEditTask(actor, task), "...")`).
  List queries go through `Scoping.Tasks(q, actor)` etc. so they are filtered in SQL.
- The page-door policies in `Program.cs` (`Policies.Permission(...)` on folders/pages) only check that the permission
  is granted at *any* scope. They are not a substitute for the service check.
- Services throw `NotFoundException`, `ForbiddenException` or `ValidationException` (all `OrbitException`).
  A file endpoint answers `NotFoundException` with a 404 and `ForbiddenException` with `AccessDeniedReason.Forbid`
  (the access-denied page, showing the message). `OrbitTools.Run` turns them into `McpException` so Claude sees the
  message.
- **Blazor components never inject a service.** A circuit's DI scope lives as long as the browser tab, so a shared
  DbContext would carry a refused change's queued audit row into the next save. Components derive from
  `OrbitComponentBase` and call services inside `Load(async s => ...)` / `Act(async s => ...)`, which `OrbitRunner`
  runs in a fresh scope acting for the circuit's user (`s.Get<TaskService>()`). `Load` maps NotFound to the not-found
  page and Forbidden to `Refusal`; `Act` shows any `OrbitException` as an error snackbar. `ApplicationDbContext`
  can't be created in a circuit's own scope (`AddOrbitDbContext`), so a directly injected service fails loudly. Page
  doors are `[Authorize]` (folder `_Imports.razor`) and `[RequirePermission(Permission.X)]`. `OrbitRouteView` checks a door
  on arriving at a page, not when only the query string changes (a filter), since the check reads the database and its
  *Authorizing* placeholder would rebuild the page. So a page must read its query through `[SupplyParameterFromQuery]`.
- Every write is audited in the same unit of work: `audit.Add(actor, AuditEntity.X, id, AuditAction.Y, departmentId,
  summary, details)` queues the row and the caller's `SaveChangesAsync` persists it. `ChangeSet` builds field-level
  `{from,to}` diffs for the details.

## Adding or changing things

- **A permission** touches `Permission.cs` (key), `PermissionCatalog` (label, group, allowed scopes), `DefaultRoles`
  (Member / Department Admin grants), `AccessPolicy` and/or `Scoping`, page conventions in `Program.cs`, and
  `Orbit.Tests/Access`. Shipped roles are seeded once and never re-applied, so existing databases get the new default
  grants only through a data step in the migration (see `AddAssets`). The built-in System Administrator gets every
  catalogue entry automatically.
- **An MCP tool** is a method on `Orbit.Web/Mcp/OrbitTools.cs` wrapped in `Run(async () => ...)`. Arguments are
  mostly strings parsed with the file's `ParseGuid`/`ParseDate`/`ParseEnum`/`TaskIdAsync` helpers, so a bad value
  gives a readable error (a schema or binding failure only reaches the client as a generic "An error occurred").
  Conventions: ids also accept human numbers (`T-26-00012`, ERP asset numbers), `"none"` clears a field, writes
  pass `TaskSource.Api`, and `create_task`/`create_asset` take an `idempotencyKey`. API keys act as the synthetic Claude user
  (`WellKnownIds.ClaudeAgentUserId`). When the tool set changes, also update `ServerInstructions` in `Program.cs`
  and the tool list in `README.md`.
- **A page** is a `.razor` under `Components/Pages/` (spec §6; the conversion from Razor Pages is §13 items 98 to 107).
  Links are literal paths (menus use `NavItem.Href`); email links fix `/Tasks/Details/{id}`, `/Projects/Details/{id}`
  and `/Requests/Request/{id}`. A file is a minimal API endpoint, not a page (`AttachmentDownloadEndpoint`, `ReportPdfEndpoint`, `TaskClockEndpoint`).
  Only Identity's pages and the error page are Razor Pages, on `Pages/Shared/_Layout.cshtml` with Bootstrap; Bootstrap
  must never be loaded on a Blazor page (it clashes with MudBlazor). The colours are in `OrbitTheme.cs`, mirrored for
  the Identity pages in `site.css`. Blazor intercepts same-site links as navigation: a link that mustn't count as
  leaving the page (the task clock stops on leave) opens in a new tab or, for a file, carries `download`. Blazor also
  drops the whitespace between an element and a code block, so put inline pieces that sit beside an `@if` in a flex row
  with a gap (`orbit-gap-row`). A list page's filters are query parameters read as strings
  (`[SupplyParameterFromQuery]`, parsed with `FilterChoice` / `FilterQuery`), and remembered with `BrowserFilterMemory`
  (a Blazor page has no HTTP response to set a cookie on): a page that remembers them inherits `OrbitListPage`, which
  holds `GoAsync`/`PageUrl`/`ResetAsync`. A key carried in the address but not remembered (the Planning page's sprint) is
  in `CarriedKeys` and changed with `GoCarried`. The Tasks list's keys are `TaskListKeys`: read its filters under them and
  build any link to it from them with `FilterQuery` (`Navigation.MyTasks`, `DashboardLinks`), never a hand-written query
  string. A date picker converts with `PickerDate`, and a Copy button is `CopyButton`. A task table is `TaskTable`: `Edits`
  (`TaskTableEdits`) picks its inline controls, and `SprintCell`, `RowActions` and `RowClass` let a page add its own, as the
  Planning page does, rather than copying the table. An HTML attribute on a MudBlazor component whose name matches
  a parameter binds the parameter (`inputmode` is `InputMode`), so pass it as C# (`InputMode="@(InputMode.@decimal)"`).
  A picked file (`IBrowserFile`) can only be read while its input is on the page, so a form sent later reads it into
  memory as it is picked (`RequestWizard`). Styles are in `css/app.css` (the `orbit-*` classes), `css/gantt.css` and
  `css/requests.css`. Name a page component so it can't clash with a model type (`SprintBoardPage`, not `SprintBoard`).
- **Entities/migrations:** every enum is stored as its name (a convention at the end of
  `ApplicationDbContext.OnModelCreating`), so raw SQL uses `'Todo'`, not `0`. Recent migrations were tested on an
  empty DB and on a DB at the previous migration, forward, back (`Down`) and forward again (spec §13 item 37).
- **Pure rules** (`Application/Scheduling/*`, `Application/Assets/*Rules`, `Application/Nagios/*`, `Application/Messages/*`, `RoleRules`,
  `DependencyRules`, `DirectoryImportRules`, `DashboardRules`, `DayPlanRules`, `DueWindowRules`, `SprintPlanningRules`) have no DB access so they can be unit-tested. Put new domain logic there when it can be
  separated. Tests cite the spec's acceptance IDs (e.g. `AST-005`) in their doc comments.
- The critical path analysis is stored and marked stale by a hash of its inputs (`ScheduleFingerprint`), not by
  write hooks. A new field that affects scheduling must be added to the fingerprint.
- **Emails are messages** (§6.7). A change that should email someone calls the matching `NotificationService` method
  (`TaskCreatedAsync`, `TaskClosedAsync`, ...) *before* its `SaveChangesAsync`: it writes `OutboundEmail` rows into
  the same unit of work and `EmailDispatchJob` sends them. A new event is a `MessageTrigger`, a `MessageCatalogue`
  entry (conditions, recipients, tokens) and a `NotificationService` method. Only Identity's account emails and the
  Admin > Messages test email call `IEmailSender` directly. The sender is `Email:Provider`: `LoggingEmailSender` (the
  default), `BrevoEmailSender`, `MailjetEmailSender` or `ResendEmailSender`. The providers derive from `ApiEmailSender`
  (the HTTP round trip) and keep their pure request/error rules in `BrevoRules` / `MailjetRules` / `ResendRules`, with
  what they share in `EmailApiRules`.
- **A job that writes tasks** (`RecurrenceService`, `NagiosMonitor`) builds the `TaskItem` itself as `Actor.System`:
  `TaskService` and `CommentService` read `IActorProvider`, which throws outside an HTTP request. A row with no
  author or creator is shown as *System*; Claude's writes carry the synthetic Claude user, never null.
- **An agent command** is a method and capability in `Orbit.Agents.Contracts/AgentProtocol.cs`, a handler and `Hello`
  entry in `Orbit.Agent/AgentWorker.cs`, and a dispatcher in the web app that asks only agents announcing the
  capability. Bump the agent's `<Version>`; deployed agents are updated rarely, so keep the logic in the web app.
  A capability can also extend an existing command with an optional request field (`ldap.list-managers`): an older
  agent ignores the field, so record which kind answered.

## Spec and documentation conventions

- Code comments and docs cite spec sections (`§6.5` roles and access, `§6.15` subtasks/dependencies, `§6.17` critical
  path, `§6.19` assets, `§6.20` requests, `§6.21` Nagios monitoring, `§7` MCP, `§8` auth). Read the relevant section
  before changing behaviour.
- A feature change updates `orbit-spec.md` along with the code: the functional section, a numbered entry in
  **§13 Decisions Log** (what was decided and why) and a step in **§14 Suggested Build Order**. User-visible changes
  also go in `README.md`.
- Prose and identifiers use British spelling (organisation, catalogue, colour, authorise).
- Line endings are mixed on disk (`core.autocrlf=true`: LF in the index, CRLF in files an IDE has touched). Keep each
  file's existing endings when editing. Check with `git ls-files --eol <file>`.

  Always keep the orbit-spec.md specification file up to date after implementing changes.