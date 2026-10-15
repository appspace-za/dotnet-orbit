namespace Orbit.Application;

public sealed class AppOptions
{
    public const string Section = "App";
    /// <summary>Public base URL used in notification emails.</summary>
    public string BaseUrl { get; set; } = "https://localhost:7179";
}

public sealed class JobOptions
{
    public const string Section = "Jobs";
    public RecurringTaskJobOptions RecurringTasks { get; set; } = new();
    public DueDateNotificationJobOptions DueDateNotifications { get; set; } = new();
    public ClockSweepJobOptions ClockSweep { get; set; } = new();
    public RequestActionJobOptions RequestActions { get; set; } = new();
    public NagiosJobOptions Nagios { get; set; } = new();
    public EmailDispatchJobOptions EmailDispatch { get; set; } = new();
}

/// <summary>
/// Schedule for the Quartz.NET job that checks the Nagios instances (§6.21). It only decides how often Orbit looks for an instance
/// that is due: how often each instance is checked is its own setting, in minutes.
/// </summary>
public sealed class NagiosJobOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Quartz cron expression (seconds first). Default: every minute.</summary>
    public string Cron { get; set; } = "0 * * * * ?";
}

/// <summary>
/// Schedule for the Quartz.NET job that runs request actions (§6.20) and reconciles request steps whose task closed. The engine
/// also fires it the moment an action becomes ready; the schedule catches what a restart or a failed hook left behind.
/// </summary>
public sealed class RequestActionJobOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Quartz cron expression (seconds first). Default: every 15 seconds.</summary>
    public string Cron { get; set; } = "*/15 * * * * ?";
}

/// <summary>Schedule for the Quartz.NET job that sends the outbox's emails (§6.7). How it retries is <see cref="EmailOptions"/>.</summary>
public sealed class EmailDispatchJobOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Quartz cron expression (seconds first). Default: every 15 seconds.</summary>
    public string Cron { get; set; } = "*/15 * * * * ?";
}

/// <summary>Schedule for the Quartz.NET recurring-task generator.</summary>
public sealed class RecurringTaskJobOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Quartz cron expression (seconds first). Default: every day at 02:00 server time.</summary>
    public string Cron { get; set; } = "0 0 2 * * ?";
    /// <summary>Also run once shortly after the app starts, so a restart never skips a day.</summary>
    public bool RunOnStartup { get; set; } = true;
}

/// <summary>Schedule for the Quartz.NET due-date notification job.</summary>
public sealed class DueDateNotificationJobOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Notify when a task is due within this many days (0 = day-of only).</summary>
    public int LeadDays { get; set; } = 1;
    /// <summary>Quartz cron expression (seconds first). Default: every day at 07:00 server time.</summary>
    public string Cron { get; set; } = "0 0 7 * * ?";
    public bool RunOnStartup { get; set; } = true;
}

/// <summary>Schedule for the Quartz.NET job that stops task clocks whose page has stopped checking in (§6.10).</summary>
public sealed class ClockSweepJobOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Quartz cron expression (seconds first). Default: every minute.</summary>
    public string Cron { get; set; } = "0 * * * * ?";
}

public sealed class SeedOptions
{
    public const string Section = "Seed";
    public SeedAdminOptions Admin { get; set; } = new();
}

/// <summary>First-run System Admin bootstrap. Consumed only when no System Admin exists yet.</summary>
public sealed class SeedAdminOptions
{
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? Password { get; set; }
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";
    /// <summary>Apply pending EF migrations on startup.</summary>
    public bool ApplyMigrations { get; set; } = true;
}

/// <summary>On-premises Orbit Agents (spec §8.2).</summary>
public sealed class AgentOptions
{
    public const string Section = "Agents";
    /// <summary>How long Orbit waits for an agent to answer a command (e.g. a directory sign-in) before trying the next agent.</summary>
    public int CommandTimeoutSeconds { get; set; } = 15;
    /// <summary>
    /// How long "Import from directory" waits for an agent to list the directory's users. Keep it under the reverse
    /// proxy's read timeout (nginx: 60 s by default): the admin's browser waits this long for the page.
    /// </summary>
    public int DirectoryListTimeoutSeconds { get; set; } = 45;
    /// <summary>How long a registration token stays redeemable after it is shown to the admin.</summary>
    public int RegistrationTokenLifetimeMinutes { get; set; } = 60;
}

/// <summary>Nagios monitoring (spec §6.21). The instances and their thresholds are data, edited under Admin &gt; Nagios.</summary>
public sealed class NagiosOptions
{
    public const string Section = "Nagios";
    /// <summary>How long an agent may spend reading one instance's status. Orbit waits ten seconds longer for its answer.</summary>
    public int QueryTimeoutSeconds { get; set; } = 30;
    /// <summary>
    /// A check is discarded when Nagios last updated its status data longer ago than this: the daemon has probably stopped, and
    /// acting on what it last knew would raise and resolve the wrong things. Raise it where status_update_interval is long.
    /// </summary>
    public int StaleAfterSeconds { get; set; } = 300;
}

/// <summary>
/// Sending the emails messages write (spec §6.7). Which emails go, to whom and saying what is data, edited under Admin &gt; Messages;
/// how they are delivered is Identity's <c>IEmailSender</c>, chosen by <see cref="Provider"/>: the log (the default), Brevo or Mailjet.
/// </summary>
public sealed class EmailOptions
{
    public const string Section = "Email";
    /// <summary>Tries before an email is given up on and marked Failed: after 1, 5, 15 and 60 minutes, then every 4 hours.</summary>
    public int MaxAttempts { get; set; } = 6;
    /// <summary>How long a sent or failed email stays in the outbox, for Admin &gt; Messages to show.</summary>
    public int KeepDays { get; set; } = 30;
    /// <summary>The most emails one run of the job sends; the rest wait for the next run.</summary>
    public int BatchSize { get; set; } = 50;
    /// <summary>
    /// Who delivers the emails. Brevo needs <see cref="FromAddress"/> and <c>Email:Brevo:ApiKey</c>; Mailjet needs it and
    /// <c>Email:Mailjet:ApiKey</c> and <c>SecretKey</c>. Both are checked at startup.
    /// </summary>
    public EmailProvider Provider { get; set; } = EmailProvider.Log;
    /// <summary>The From address: a sender the provider has verified, or an address on a domain authenticated there.</summary>
    public string? FromAddress { get; set; }
    /// <summary>The From name. Brevo takes at most 70 characters.</summary>
    public string FromName { get; set; } = "Orbit";
    public BrevoOptions Brevo { get; set; } = new();
    public MailjetOptions Mailjet { get; set; } = new();
}

public enum EmailProvider
{
    /// <summary>Write each email to the log and send nothing (<c>LoggingEmailSender</c>).</summary>
    Log,
    /// <summary>Send through Brevo's transactional email API (<c>BrevoEmailSender</c>).</summary>
    Brevo,
    /// <summary>Send through Mailjet's Send API v3.1 (<c>MailjetEmailSender</c>).</summary>
    Mailjet
}

/// <summary>Brevo's transactional email API (spec §6.7), used when <c>Email:Provider</c> is Brevo.</summary>
public sealed class BrevoOptions
{
    /// <summary>A v3 API key (<c>xkeysib-...</c>). A secret: supply it as <c>Email__Brevo__ApiKey</c>, never in appsettings.json.</summary>
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://api.brevo.com/v3/";
    /// <summary>How long one send may take before it counts as failed and is tried again later.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>Mailjet's Send API v3.1 (spec §6.7), used when <c>Email:Provider</c> is Mailjet.</summary>
public sealed class MailjetOptions
{
    /// <summary>The API key (Mailjet's public key), sent with <see cref="SecretKey"/> as Basic credentials.</summary>
    public string? ApiKey { get; set; }
    /// <summary>The API key's secret key. A secret: supply it as <c>Email__Mailjet__SecretKey</c>, never in appsettings.json.</summary>
    public string? SecretKey { get; set; }
    /// <summary>An account Mailjet hosts in the US uses <c>https://api.us.mailjet.com/v3.1/</c>.</summary>
    public string BaseUrl { get; set; } = "https://api.mailjet.com/v3.1/";
    /// <summary>How long one send may take before it counts as failed and is tried again later.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>Sign-in hardening (spec §8.3). Orbit is on the internet and, for directory users, is a door onto Active Directory.</summary>
public sealed class SecurityOptions
{
    public const string Section = "Security";
    public LockoutSettings Lockout { get; set; } = new();
    public LoginThrottleSettings LoginThrottle { get; set; } = new();
}

/// <summary>
/// Per-account lockout, for local and directory users alike. For a directory user every wrong guess at Orbit is a real
/// failed bind in Active Directory and counts towards AD's own lockout - so Orbit must lock FIRST, or anyone who knows
/// a colleague's email could lock their Windows account from the internet. A locked Orbit account is refused without
/// the directory being contacted, so AD sees at most <see cref="MaxFailedAttempts"/> bad binds per <see cref="LockoutMinutes"/>.
/// Keep MaxFailedAttempts below AD's lockout threshold and LockoutMinutes at least AD's "reset lockout counter after".
/// </summary>
public sealed class LockoutSettings
{
    public int MaxFailedAttempts { get; set; } = 3;
    public int LockoutMinutes { get; set; } = 30;
}

/// <summary>
/// Per-address limit on FAILED sign-ins. Per-account lockout can't see one password being tried across many accounts;
/// this can. Successful sign-ins cost nothing, so an office sharing one address isn't penalised for signing in.
/// </summary>
public sealed class LoginThrottleSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxFailures { get; set; } = 20;
    public int WindowMinutes { get; set; } = 15;
}

public sealed class DataProtectionOptions
{
    public const string Section = "DataProtection";
    /// <summary>Directory for the key ring. Leave empty to use the platform default.</summary>
    public string? KeyRingPath { get; set; }
}

/// <summary>Critical path analysis thresholds (spec §6.17). The working week and calendar exceptions are data, edited under Admin &gt; Working Calendar.</summary>
public sealed class CriticalPathOptions
{
    public const string Section = "CriticalPath";
    /// <summary>A task whose total float is between 1 and this many working days is near-critical.</summary>
    public int NearCriticalThresholdWorkingDays { get; set; } = 5;
    /// <summary>Buffer consumption up to this percentage is Green.</summary>
    public int BufferAmberPercent { get; set; } = 33;
    /// <summary>Buffer consumption above the amber limit and up to this percentage is Amber; beyond it, Red.</summary>
    public int BufferRedPercent { get; set; } = 66;
    /// <summary>Hands-on hours a working day is taken to hold when an estimate is compared with its planning window.</summary>
    public int HoursPerWorkingDay { get; set; } = 8;
}

/// <summary>The asset register's two windows (spec §6.19). Check intervals are per asset type, edited on the Asset types pages.</summary>
public sealed class AssetOptions
{
    public const string Section = "Assets";
    /// <summary>A check due within this many days (today included) is "due soon".</summary>
    public int CheckDueSoonDays { get; set; } = 14;
    /// <summary>A warranty ending within this many days is "expiring".</summary>
    public int WarrantyExpiringDays { get; set; } = 60;
}

/// <summary>
/// Request actions (spec §6.20): how long a script may run, and the named database connections a script running on the web server may
/// open (<c>Actions:Connections:erp:Provider</c> = postgres or sqlserver, <c>:ConnectionString</c>). The connection named <c>orbit</c> is
/// always Orbit's own database. An agent-side action takes its connections from the agent's <c>actions.json</c> instead.
/// </summary>
public sealed class ActionOptions
{
    public const string Section = "Actions";
    /// <summary>The name under which a web-side script reaches Orbit's own database.</summary>
    public const string OrbitConnection = "orbit";
    public int TimeoutSeconds { get; set; } = 120;
    public Dictionary<string, ActionConnectionOptions> Connections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ActionConnectionOptions
{
    public string Provider { get; set; } = "postgres";
    public string ConnectionString { get; set; } = string.Empty;
}

/// <summary>File attachments on tasks and projects (spec §6.18). The bytes live in the database, so there is nothing to configure but the limits.</summary>
public sealed class AttachmentOptions
{
    public const string Section = "Attachments";
    /// <summary>Largest single file accepted, in megabytes. A file is held in memory while it is saved and served, so keep this modest.</summary>
    public int MaxFileSizeMb { get; set; } = 25;
    /// <summary>How many files one upload may carry.</summary>
    public int MaxFilesPerUpload { get; set; } = 5;
    /// <summary>
    /// Largest attachment the get_attachment MCP tool returns, in megabytes; a bigger file is described but not returned.
    /// The whole file lands in the model's context (a third larger again as base64), so keep this small.
    /// </summary>
    public int MaxMcpFileSizeMb { get; set; } = 5;
    public long MaxFileSizeBytes => (long)MaxFileSizeMb * 1024 * 1024;
    public long MaxMcpFileSizeBytes => (long)MaxMcpFileSizeMb * 1024 * 1024;
    /// <summary>The request body an upload may need: every file at the limit, plus room for the form itself.</summary>
    public long MaxUploadBodyBytes => MaxFilesPerUpload * MaxFileSizeBytes + 1024 * 1024;
}
