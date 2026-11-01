using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Helpers;

/// <summary>Small presentation helpers shared by the pages: dates, names, money, sizes and links.</summary>
public static class Ui
{
    public static string SourceLabel(TaskSource s) => s switch
    {
        TaskSource.Api => "Claude",
        TaskSource.Recurring => "Recurring",
        TaskSource.Request => "Request",
        TaskSource.Nagios => "Nagios",
        _ => "Manual"
    };

    /// <summary>"Laptop · Head office · Jane Smith, Ann Lee +1 · Damaged": what tells two similar assets apart in a picker (§6.19).</summary>
    public static string AssetDetail(string type, string? location, IReadOnlyList<string> holders, AssetStatus status)
    {
        var parts = new List<string> { type };
        if (location is not null) parts.Add(location);
        if (holders.Count > 0) parts.Add(string.Join(", ", holders.Take(2)) + (holders.Count > 2 ? $" +{holders.Count - 2}" : ""));
        if (status != AssetStatus.Active) parts.Add(status.Label());
        return string.Join(" · ", parts);
    }

    /// <summary>How a task names its asset (§6.19): the ERP number and name, or just the name.</summary>
    public static string AssetLabel(Asset asset) => Orbit.Application.Assets.AssetRules.Label(asset.AssetNumber, asset.Name);

    /// <summary>A purchase value in the organisation's one currency (§6.19): "18,500.00".</summary>
    public static string Money(decimal? value) =>
        value?.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "-";

    public static string ScopeLabel(PermissionScope s) => s.Label();

    public static string When(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public static string When(DateTime? utc) => utc is null ? "-" : When(utc.Value);

    public static string Day(DateOnly? d) => d?.ToString("yyyy-MM-dd") ?? "-";

    /// <summary>A sprint picker's label (§6.3): its name, status and dates, as on the Backlog and the Planning page.</summary>
    public static string SprintLabel(Sprint s) => $"{s.Name} ({s.Status}, {Day(s.StartDate)} - {Day(s.EndDate)})";

    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        return When(utc);
    }

    /// <summary>A file size for people: "812 B", "12.4 KB", "3.1 MB".</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };

    public static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Length <= max ? s : s[..max].TrimEnd() + "...";

    /// <summary>
    /// A description or comment with its web addresses as links that open in a new tab (§6.2, <see cref="LinkRules"/>); the
    /// rest is encoded as <c>@</c> would. <c>noreferrer</c> keeps Orbit's own addresses out of the other site's logs.
    /// </summary>
    public static IHtmlContent Linkify(string? text)
    {
        var html = new HtmlContentBuilder();
        foreach (var segment in LinkRules.Split(text))
        {
            if (!segment.IsLink)
            {
                html.Append(segment.Text);
                continue;
            }
            var link = new TagBuilder("a");
            link.Attributes["href"] = segment.Text;
            link.Attributes["target"] = "_blank";
            link.Attributes["rel"] = "noopener noreferrer";
            link.InnerHtml.Append(segment.Text);
            html.AppendHtml(link);
        }
        return html;
    }

    /// <summary><see cref="Linkify"/> as an HTML string, for a Blazor component (<c>@((MarkupString)Ui.LinkifyHtml(text))</c>).</summary>
    public static string LinkifyHtml(string? text)
    {
        using var writer = new StringWriter();
        Linkify(text).WriteTo(writer, System.Text.Encodings.Web.HtmlEncoder.Default);
        return writer.ToString();
    }

    /// <summary>
    /// Who wrote or created something. Nobody means Orbit itself - a job such as the recurring-task generator or the Nagios
    /// monitor (§6.21); what Claude writes over MCP is the synthetic Claude user's, never nobody's.
    /// </summary>
    public static string AuthorName(ApplicationUser? user) =>
        user is null ? "System" : user.IsSystemAccount ? "Claude" : user.DisplayName;

    public static string ActionLabel(string action) => action switch
    {
        "Created" => "created",
        "Updated" => "updated",
        "StatusChanged" => "changed status of",
        "Completed" => "completed",
        "Archived" => "archived",
        "Unarchived" => "unarchived",
        "SprintChanged" => "re-planned",
        "Started" => "started",
        "CommentAdded" => "commented on",
        "AttachmentAdded" => "attached a file to",
        "AttachmentRemoved" => "removed an attachment from",
        "TimeLogged" => "logged time on",
        "TimeUpdated" => "edited a time entry on",
        "TimeDeleted" => "deleted a time entry on",
        "ClockStarted" => "started the clock on",
        "ClockStopped" => "stopped the clock on",
        "Planned" => "planned for the day",
        "Unplanned" => "took off the day plan",
        "ParentChanged" => "changed the parent of",
        "DependencyAdded" => "added a dependency on",
        "DependencyRemoved" => "removed a dependency on",
        "Paused" => "paused",
        "Resumed" => "resumed",
        "Generated" => "generated",
        "Deactivated" => "deactivated",
        "Reactivated" => "reactivated",
        "PasswordReset" => "reset the password of",
        "Revoked" => "revoked",
        "Unlocked" => "unlocked",
        "Registered" => "registered",
        "Deleted" => "deleted",
        "CriticalPathAnalysed" => "ran a critical path analysis on",
        "ExceptionAdded" => "added a calendar exception to the",
        "ExceptionRemoved" => "removed a calendar exception from the",
        "AssigneeAdded" => "added an assignee to",
        "AssigneeRemoved" => "removed an assignee from",
        "AssetAssigned" => "assigned",
        "AssetUnassigned" => "unassigned",
        "CheckRecorded" => "recorded a check on",
        "CheckRemoved" => "removed a check from",
        "Approved" => "approved a step of",
        "Declined" => "declined",
        "Retried" => "retried a step of",
        "Skipped" => "skipped a step of",
        "Cancelled" => "cancelled",
        _ => action.ToLowerInvariant()
    };

    /// <summary>Status options the actor may pick for this task in an inline control: every status if they may change it (§6.5), otherwise only the current one.</summary>
    public static IReadOnlyList<TaskItemStatus> AllowedStatuses(Actor actor, TaskItem task) =>
        AccessPolicy.CanChangeStatus(actor, task) ? Enum.GetValues<TaskItemStatus>() : [task.Status];

    /// <summary>A lag in days as "+2 d" / "-1 d", or "-" for none.</summary>
    public static string Lag(int lagDays) => lagDays == 0 ? "-" : $"{(lagDays > 0 ? "+" : "")}{lagDays} d";
}
