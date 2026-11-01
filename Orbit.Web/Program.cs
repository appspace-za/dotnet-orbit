using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol.AspNetCore;
using Orbit.Agents;
using Orbit.Agents.Contracts;
using Orbit.Application;
using Orbit.Application.Messages;
using Orbit.Application.Services;
using Orbit.Auth;
using Orbit.Components;
using Orbit.Components.Infrastructure;
using Orbit.Components.Pages.Reports;
using Orbit.Components.Shared;
using Orbit.Components.Tasks;
using Orbit.Data;
using Orbit.Data.Entities;
using Orbit.Jobs;
using Orbit.Mcp;
using MudBlazor;
using MudBlazor.Services;
using Orbit.Scripting;
using QuestPDF.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// --- Data ---------------------------------------------------------------------------------
var connectionString = config.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddOrbitDbContext(connectionString); // refused in a Blazor circuit's own scope (CircuitScopeGuard)
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.Configure<AppOptions>(config.GetSection(AppOptions.Section));
builder.Services.Configure<JobOptions>(config.GetSection(JobOptions.Section));
builder.Services.Configure<SeedOptions>(config.GetSection(SeedOptions.Section));
builder.Services.Configure<DatabaseOptions>(config.GetSection(DatabaseOptions.Section));
builder.Services.Configure<AgentOptions>(config.GetSection(AgentOptions.Section));
builder.Services.Configure<SecurityOptions>(config.GetSection(SecurityOptions.Section));
builder.Services.Configure<CriticalPathOptions>(config.GetSection(CriticalPathOptions.Section));
builder.Services.Configure<AttachmentOptions>(config.GetSection(AttachmentOptions.Section));
builder.Services.Configure<AssetOptions>(config.GetSection(AssetOptions.Section));
builder.Services.Configure<ActionOptions>(config.GetSection(ActionOptions.Section));
builder.Services.Configure<NagiosOptions>(config.GetSection(NagiosOptions.Section));
builder.Services.Configure<EmailOptions>(config.GetSection(EmailOptions.Section));
var security = config.GetSection(SecurityOptions.Section).Get<SecurityOptions>() ?? new SecurityOptions();

// Behind nginx/Caddy on the same host (deploy/README.md) the app only ever sees 127.0.0.1 over plain http.
// Honour X-Forwarded-For/Proto - from loopback proxies only, the default - so the request scheme and the client
// address are the real ones. The address matters twice: it is shown for each agent, and the sign-in throttle counts by it.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

// Persisted key ring (production): without it every restart signs everyone out. See deploy/README.md.
var keyRingPath = config[$"{Orbit.Application.DataProtectionOptions.Section}:KeyRingPath"];
if (!string.IsNullOrWhiteSpace(keyRingPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
        .SetApplicationName("Orbit");
}

// --- Identity (interactive users) ---------------------------------------------------------
builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false; // accounts are created by a System Admin
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.AllowedForNewUsers = true;
        // Stricter than Identity's 5 attempts / 5 minutes, and deliberately stricter than Active Directory's own policy:
        // for a directory user each wrong guess here is a failed bind there, so Orbit has to lock first (see LockoutSettings).
        options.Lockout.MaxFailedAccessAttempts = Math.Max(1, security.Lockout.MaxFailedAttempts);
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(Math.Max(1, security.Lockout.LockoutMinutes));
    })
    .AddRoles<ApplicationRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddClaimsPrincipalFactory<OrbitClaimsPrincipalFactory>()
    .AddSignInManager<OrbitSignInManager>(); // directory (LDAP) users are checked against AD via an Orbit Agent

// Re-validate the cookie against the security stamp often so role/department changes and deactivation bite quickly.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Identity/Account/Login";
    o.AccessDeniedPath = "/Identity/Account/AccessDenied";
    o.ExpireTimeSpan = TimeSpan.FromDays(7);
    o.SlidingExpiration = true;
});

// --- API keys (MCP / machine callers) and Orbit Agents --------------------------------------
builder.Services.AddAuthentication()
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationDefaults.Scheme, null)
    .AddScheme<AgentAuthenticationOptions, AgentAuthenticationHandler>(AgentAuthenticationDefaults.Scheme, null);

// One page-door policy per permission (spec §6.5): granted at any scope opens the page, the service applies the scope.
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
var authorization = builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.McpApiKey, p => p
        .AddAuthenticationSchemes(ApiKeyAuthenticationDefaults.Scheme)
        .RequireAuthenticatedUser())
    .AddPolicy(Policies.Agent, p => p
        .AddAuthenticationSchemes(AgentAuthenticationDefaults.Scheme)
        .RequireClaim(OrbitClaims.AgentId));
foreach (var permission in PermissionCatalog.All)
    authorization.AddPolicy(Policies.Permission(permission.Key), p => p.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(permission.Key)));

// --- Razor Pages --------------------------------------------------------------------------
builder.Services.AddRazorPages(options =>
    {
        options.Conventions.AuthorizeFolder("/");
        options.Conventions.AllowAnonymousToPage("/Error");
    })
    .AddMvcOptions(o => o.Filters.Add(new DirectoryAccountPageFilter()));

// --- Blazor (interactive server, MudBlazor) ----------------------------------------------
// The pages are components, at the URLs the Razor pages had (spec §6). A circuit's DI scope lasts as long as the browser tab,
// so components call services only through OrbitRunner, which gives each operation a scope of its own; HttpActorProvider
// refuses the circuit's own scope (ActorPrincipal.IsCircuit).
// A bound text box sends its whole value in one message when it changes (and orbit.tokens.insert returns a box's whole text),
// so SignalR's 32 KB default would drop the connection on a long description or script, losing what was typed. 4 MB is what
// a Razor form allowed for one field (FormOptions.ValueLengthLimit). The agent hub has its own limit, below.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment())
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 4 * 1024 * 1024);
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, OrbitRevalidatingAuthStateProvider>();
builder.Services.AddScoped<CircuitHandler, CircuitScopeMarker>();
builder.Services.AddScoped<ActorPrincipal>();
builder.Services.AddScoped<OrbitRunner>();
builder.Services.AddScoped<BrowserFilterMemory>();
builder.Services.AddMudServices(o =>
{
    o.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    o.SnackbarConfiguration.VisibleStateDuration = 4000;
    o.SnackbarConfiguration.ShowCloseIcon = true;
});

// --- Application services -----------------------------------------------------------------
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IActorProvider, HttpActorProvider>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<EmailDispatcher>();
builder.Services.AddScoped<MessageTemplateService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<TaskStructureService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<CommentService>();
builder.Services.AddScoped<NumberingService>();
builder.Services.AddScoped<AttachmentService>();
builder.Services.AddScoped<SprintService>();
builder.Services.AddScoped<SprintPlanningService>();
builder.Services.AddScoped<RecurrenceService>();
builder.Services.AddScoped<TimeEntryService>();
builder.Services.AddScoped<DepartmentService>();
builder.Services.AddScoped<UserDirectoryService>();
builder.Services.AddScoped<UserAdminService>();
builder.Services.AddScoped<RoleService>();
builder.Services.AddScoped<ApiKeyService>();
builder.Services.AddScoped<AgentService>();
builder.Services.AddScoped<LdapSettingsService>();
builder.Services.AddScoped<DirectoryAuthService>();
builder.Services.AddScoped<DirectoryImportService>();
builder.Services.AddScoped<ReportingService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<WorkingCalendarService>();
builder.Services.AddScoped<CriticalPathService>();
builder.Services.AddScoped<AssetService>();
builder.Services.AddScoped<AssetTypeService>();
builder.Services.AddScoped<AssetLocationService>();
builder.Services.AddScoped<RequestCatalogueService>();
builder.Services.AddScoped<RequestActionService>();
builder.Services.AddScoped<RequestService>();
builder.Services.AddScoped<RequestEngine>();
builder.Services.AddScoped<AgentScriptDispatcher>();
// Nagios monitoring (§6.21): the settings pages' service, the monitor the job runs, and the dispatcher both read Nagios through.
builder.Services.AddScoped<NagiosService>();
builder.Services.AddScoped<NagiosMonitor>();
builder.Services.AddScoped<AgentNagiosDispatcher>();
builder.Services.AddSingleton<NagiosPasswordProtector>();
// One script host per process (§6.20): compiled scripts are cached in it.
builder.Services.AddSingleton<ScriptHost>();
// The email sender (§6.7): Brevo's, Mailjet's or Resend's API, as Email:Provider says, otherwise the log. Settings that won't send
// stop startup here, and the header carrying the provider's credentials is kept out of HttpClient's logging.
var email = config.GetSection(EmailOptions.Section).Get<EmailOptions>() ?? new EmailOptions();
var emailProblems = email.Provider switch
{
    EmailProvider.Brevo => BrevoRules.ConfigProblems(email),
    EmailProvider.Mailjet => MailjetRules.ConfigProblems(email),
    EmailProvider.Resend => ResendRules.ConfigProblems(email),
    _ => []
};
if (emailProblems.Count > 0) throw new InvalidOperationException($"Email:Provider is {email.Provider}, but {string.Join("; ", emailProblems)}.");
switch (email.Provider)
{
    case EmailProvider.Brevo:
        builder.Services.AddHttpClient<IEmailSender, BrevoEmailSender>(c =>
            {
                c.BaseAddress = BrevoRules.BaseUri(email.Brevo);
                c.Timeout = TimeSpan.FromSeconds(Math.Max(5, email.Brevo.TimeoutSeconds));
            })
            .RedactLoggedHeaders([BrevoRules.ApiKeyHeader]);
        break;
    case EmailProvider.Mailjet:
        builder.Services.AddHttpClient<IEmailSender, MailjetEmailSender>(c =>
            {
                c.BaseAddress = MailjetRules.BaseUri(email.Mailjet);
                c.Timeout = TimeSpan.FromSeconds(Math.Max(5, email.Mailjet.TimeoutSeconds));
            })
            .RedactLoggedHeaders([HeaderNames.Authorization]);
        break;
    case EmailProvider.Resend:
        builder.Services.AddHttpClient<IEmailSender, ResendEmailSender>(c =>
            {
                c.BaseAddress = ResendRules.BaseUri(email.Resend);
                c.Timeout = TimeSpan.FromSeconds(Math.Max(5, email.Resend.TimeoutSeconds));
            })
            .RedactLoggedHeaders([HeaderNames.Authorization]);
        break;
    default:
        builder.Services.AddTransient<IEmailSender, LoggingEmailSender>();
        break;
}

// --- MCP server (Streamable HTTP, stateless) ----------------------------------------------
builder.Services.AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "Orbit", Version = "1.0.0" };
        o.ServerInstructions = "Orbit is the company's task and project tracker. Use list_departments / list_users / list_projects " +
            "to resolve ids before creating or updating tasks. Every write you make is tagged as API-created and audited. " +
            "For a project's critical path, task float and project-buffer status use get_critical_path - Orbit's stored, deterministic analysis - " +
            "rather than deriving criticality from raw tasks; run_critical_path_analysis runs and stores a fresh one. " +
            "Files attached to tasks and projects are listed by get_task / get_project and read with get_attachment. " +
            "Tasks, projects and assets each have a comment thread: add_comment / list_comments take exactly one of taskId, projectId or assetId; " +
            "get_task includes the thread, get_project and get_asset report a commentCount. " +
            "log_time records time a person worked on a task: pass their userId from list_users, and an idempotencyKey so a retry can't log it twice. " +
            "The asset register (laptops, vehicles, equipment) belongs to no project: list_assets / get_asset read it, " +
            "create_asset / update_asset / record_asset_check write it; each department has its own asset types and locations " +
            "(list_asset_types, list_asset_locations; get_, create_ and update_asset_type / asset_location read and manage them, adding and changing " +
            "a type's properties but never deleting one), and any assetId argument also accepts the asset's ERP asset number, which not every asset has. " +
            "Pass an idempotencyKey to create_asset so a retry can't register an asset twice. " +
            "To register, change or check many assets use create_assets / update_assets / record_asset_checks - up to 100 items a call, " +
            "each saved or refused on its own and reported by index - rather than one call per asset. " +
            "A task can be about one asset - a repair, a service: pass assetId to create_task / update_task (\"none\" unlinks it); " +
            "get_asset lists the asset's task history and list_tasks filters on assetId. " +
            "A task has any number of assignees, who do it - one person, several who share it, or nobody yet: pass their userIds from " +
            "list_users as assigneeIds to create_task; on update_task assigneeIds is the complete new set ([] unassigns everyone, omitted " +
            "leaves them alone), and every task result lists them as assignees (id, name, active). list_tasks filters on one assigneeId, " +
            "or on unassigned. " +
            "A task may also have a requestee, whom it is for: to log a task on someone's behalf " +
            "(\"Bob asked for ...\") pass their userId from list_users as requesteeId to create_task; update_task changes it (\"none\" clears it) " +
            "and list_tasks filters on it (\"none\" for the tasks created for nobody else). The requestee sees and updates the task as their own. " +
            "list_tasks also takes projectId \"none\" for the standalone tasks, on no project. " +
            "For sprint planning a task is in a sprint, on the Backlog (work to discuss and plan into a sprint at sprint planning) or " +
            "Not Planned (work that came up during a sprint, such as support): create_task puts it on the Backlog when it has a projectId and " +
            "makes it Not Planned otherwise, and sprintId on create_task / update_task / list_tasks takes a sprint id, \"backlog\" or \"none\" (Not Planned). " +
            "Planning a task into a sprint, on create_task as on update_task, needs the key's Plan tasks permission. " +
            "Setting a task Done needs solutionNotes on update_task - how it was resolved, kept apart from the comment thread - unless " +
            "it already has them; they are optional for Cancelled, and every task result carries them as solutionNotes. " +
            "A task with source Nagios was raised by Orbit's monitoring for a host or service Nagios reported down (list_tasks filters on " +
            "source); Orbit adds a comment, author System, when Nagios reports it well again, and leaves closing the task to a person.";
    })
    .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<OrbitTools>();

// --- Orbit Agents (on-premises connector, outbound SignalR connection) ----------------------
// A directory listing for the user import comes back as one message, far above SignalR's 32 KB default (about 500 bytes
// a person with their manager's DN, at most DirectoryImportService.MaxUsers people). Only an authenticated agent can reach
// this hub at all.
builder.Services.AddSignalR().AddHubOptions<AgentHub>(o => o.MaximumReceiveMessageSize = 8 * 1024 * 1024);
builder.Services.AddSingleton<AgentConnectionRegistry>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // One shared window rather than one per client address: registration is a rare, admin-initiated event.
    o.AddFixedWindowLimiter(AgentEndpoints.RegistrationRateLimit, w =>
    {
        w.PermitLimit = 10;
        w.Window = TimeSpan.FromMinutes(1);
        w.QueueLimit = 0;
    });
});

// --- Background jobs (Quartz.NET, cron-scheduled from Jobs:*) ------------------------------
builder.Services.AddOrbitJobs(config);

QuestPDF.Settings.License = LicenseType.Community;

var app = builder.Build();

// --- Pipeline -----------------------------------------------------------------------------
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
// Every cookie - session, two-factor, antiforgery, TempData (which carries one-time password-reset links) - is marked
// Secure outside development, whatever scheme the app thinks the request had. Relying on the reverse proxy to forward
// the scheme correctly is one misconfiguration away from session cookies travelling over plain http.
if (!app.Environment.IsDevelopment())
    app.UseCookiePolicy(new CookiePolicyOptions { Secure = CookieSecurePolicy.Always });
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery(); // the Blazor endpoints' forms; Razor Pages check their own tokens

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapTaskClockEndpoint(); // the task page's clock, stopped by the browser's beacon on leaving (§6.10)
app.MapAttachmentDownloadEndpoint(); // an attachment's file (§6.18)
app.MapReportPdfEndpoint(); // a report's PDF (§12)
app.MapMcp("/mcp").RequireAuthorization(Policies.McpApiKey);
app.MapHub<AgentHub>(AgentProtocol.HubPath); // authorised by [Authorize(Policy = Policies.Agent)] on the hub
app.MapAgentEndpoints();

await DbInitializer.InitializeAsync(app.Services);

app.Run();
