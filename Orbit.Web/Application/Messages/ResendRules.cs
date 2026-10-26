using System.Text.Json;

namespace Orbit.Application.Messages;

/// <summary>
/// The body of Resend's <c>POST emails</c>: one email, sent now, as HTML. Resend writes the plain-text part from the HTML when none
/// is given.
/// </summary>
public sealed record ResendSendRequest(string From, IReadOnlyList<string> To, string Subject, string Html);

/// <summary>
/// Sending through Resend's email API (spec §6.7): whether the settings will do, the request for one email, and what a refusal
/// said, in words for the outbox's error. No HTTP - <c>ResendEmailSender</c> sends what these build. The API key travels only in
/// the request's Bearer <c>Authorization</c> header, never in a body or a message.
/// </summary>
public static class ResendRules
{
    public const string Provider = "Resend";
    /// <summary>Relative to <see cref="BaseUri"/>.</summary>
    public const string SendPath = "emails";
    /// <summary>Resend may block a request that carries no User-Agent (a 403, error 1010), and HttpClient sends none of its own.</summary>
    public const string UserAgent = "Orbit";

    /// <summary>The characters a name may hold, besides letters, digits and spaces, and still go unquoted (RFC 5322's atext).</summary>
    private const string AtomSymbols = "!#$%&'*+-/=?^_`{|}~";

    /// <summary>
    /// MSG-024: what is wrong with the settings for sending through Resend, each naming its key - empty when they will do. The base
    /// address must be https, except on this machine (a stand-in for Resend while testing), so the key never crosses a network in clear.
    /// </summary>
    public static IReadOnlyList<string> ConfigProblems(EmailOptions options)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Resend.ApiKey)) problems.Add("Email:Resend:ApiKey is not set");
        problems.AddRange(EmailApiRules.FromProblems(options));
        if (EmailApiRules.BaseUrlProblem("Email:Resend:BaseUrl", options.Resend.BaseUrl) is { } url) problems.Add(url);
        return problems;
    }

    /// <summary>The API's base address, ending in a slash so that <see cref="SendPath"/> is added to it rather than replacing its last part.</summary>
    public static Uri BaseUri(ResendOptions options) => EmailApiRules.BaseUri(options.BaseUrl);

    /// <summary>
    /// MSG-025: the From as Resend takes it, one string: <c>Orbit &lt;orbit@example.com&gt;</c>, the address alone when there is no
    /// name, and the name quoted when it holds anything but letters, digits, spaces and RFC 5322's atom symbols (a comma, a full stop),
    /// with its backslashes and quotes escaped. A line break or other control character in the name is a space, and spaces are single.
    /// </summary>
    public static string From(EmailOptions options)
    {
        var address = options.FromAddress!.Trim();
        var name = string.Join(' ', new string((options.FromName ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) return address;
        return name.All(c => char.IsLetterOrDigit(c) || c == ' ' || AtomSymbols.Contains(c))
            ? $"{name} <{address}>"
            : $"\"{name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" <{address}>";
    }

    /// <summary>MSG-025: the request that sends one email from the configured sender to one address.</summary>
    public static ResendSendRequest Request(EmailOptions options, string to, string subject, string html) =>
        new(From(options), [to.Trim()], subject, html);

    /// <summary>
    /// MSG-026: a refusal in words, for the outbox's error and the test email's message - Resend's error name and message when the
    /// body is its JSON error (<c>{"statusCode":401,"name":"validation_error","message":"API key is invalid"}</c>), otherwise the
    /// start of the body. Every failure is tried again, so none of them says it is final.
    /// </summary>
    public static string Describe(int status, string? body)
    {
        var root = EmailApiRules.ParseObject(body);
        return EmailApiRules.Describe(Provider, status, EmailApiRules.Text(root, "name"), EmailApiRules.Text(root, "message"), body);
    }

    /// <summary>MSG-026: the id Resend gave an email it accepted, for the log; null when the answer doesn't carry one.</summary>
    public static string? MessageId(string? body) => EmailApiRules.Text(EmailApiRules.ParseObject(body), "id");
}
