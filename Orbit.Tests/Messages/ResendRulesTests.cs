using System.Text.Json;
using Orbit.Application;
using Orbit.Application.Messages;

namespace Orbit.Tests.Messages;

/// <summary>Sending through Resend's email API (spec §6.7).</summary>
public class ResendRulesTests
{
    private const string Key = "re_0123456789abcdef";

    private static EmailOptions Settings(Action<EmailOptions>? change = null)
    {
        var options = new EmailOptions
        {
            Provider = EmailProvider.Resend,
            FromAddress = "orbit@example.com",
            FromName = "Orbit",
            Resend = new ResendOptions { ApiKey = Key }
        };
        change?.Invoke(options);
        return options;
    }

    /// <summary>MSG-024: settings that will send pass; each that won't is named by its key.</summary>
    [Fact]
    public void Config_problems_name_what_is_missing_or_wrong()
    {
        Assert.Empty(ResendRules.ConfigProblems(Settings()));
        Assert.Empty(ResendRules.ConfigProblems(Settings(o => o.FromName = "")));
        Assert.Empty(ResendRules.ConfigProblems(Settings(o => o.FromName = new string('x', 200))));
        Assert.Empty(ResendRules.ConfigProblems(Settings(o => o.Resend.BaseUrl = "http://localhost:5199")));

        Assert.Contains("Email:Resend:ApiKey", Assert.Single(ResendRules.ConfigProblems(Settings(o => o.Resend.ApiKey = " "))));
        Assert.Contains("Email:FromAddress is not set", Assert.Single(ResendRules.ConfigProblems(Settings(o => o.FromAddress = null))));
        Assert.Contains("not an email address", Assert.Single(ResendRules.ConfigProblems(Settings(o => o.FromAddress = "orbit"))));
        Assert.Contains("not an email address", Assert.Single(ResendRules.ConfigProblems(Settings(o => o.FromAddress = "Orbit <orbit@example.com>"))));
        Assert.Contains("Email:Resend:BaseUrl", Assert.Single(ResendRules.ConfigProblems(Settings(o => o.Resend.BaseUrl = "http://api.resend.com/"))));
        Assert.Contains("Email:Resend:BaseUrl", Assert.Single(ResendRules.ConfigProblems(Settings(o => o.Resend.BaseUrl = "api.resend.com"))));
        Assert.Equal(2, ResendRules.ConfigProblems(Settings(o => { o.Resend.ApiKey = null; o.FromAddress = null; })).Count);

        Assert.Equal("https://api.resend.com/emails", new Uri(ResendRules.BaseUri(new ResendOptions { BaseUrl = "https://api.resend.com" }), ResendRules.SendPath).ToString());
        Assert.Equal("https://api.resend.com/emails", new Uri(ResendRules.BaseUri(new ResendOptions()), ResendRules.SendPath).ToString());
    }

    /// <summary>MSG-025: one email is Resend's from, one recipient, subject and HTML, in its field names, with no empty fields and no key.</summary>
    [Fact]
    public void Request_is_the_sender_recipient_subject_and_html()
    {
        var json = JsonSerializer.Serialize(ResendRules.Request(Settings(o => o.FromAddress = " orbit@example.com "), " ann@example.com ",
            "Task T-26-00012 assigned", "<p>Hi</p>\n"), OrbitJson.Options);
        Assert.DoesNotContain(Key, json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(["from", "to", "subject", "html"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Orbit <orbit@example.com>", root.GetProperty("from").GetString());
        Assert.Equal("ann@example.com", Assert.Single(root.GetProperty("to").EnumerateArray()).GetString());
        Assert.Equal("Task T-26-00012 assigned", root.GetProperty("subject").GetString());
        Assert.Equal("<p>Hi</p>\n", root.GetProperty("html").GetString());
    }

    /// <summary>MSG-025: the From is one string - the name bare when it is plain words, quoted when it holds more, left out when blank.</summary>
    [Fact]
    public void From_quotes_the_name_only_when_it_must()
    {
        string From(string? name) => ResendRules.From(Settings(o => o.FromName = name!));

        Assert.Equal("Orbit <orbit@example.com>", From(" Orbit "));
        Assert.Equal("IT Help-desk <orbit@example.com>", From("IT Help-desk"));
        Assert.Equal("Zoë's Orbit <orbit@example.com>", From("Zoë's Orbit"));
        Assert.Equal("orbit@example.com", From(""));
        Assert.Equal("orbit@example.com", From(" "));
        Assert.Equal("orbit@example.com", From(null));
        Assert.Equal("\"Appspace, Orbit\" <orbit@example.com>", From("Appspace, Orbit"));
        Assert.Equal("\"Orbit Inc.\" <orbit@example.com>", From("Orbit Inc."));
        Assert.Equal("\"Orbit (IT)\" <orbit@example.com>", From("Orbit (IT)"));
        Assert.Equal("\"The \\\"Orbit\\\" app\" <orbit@example.com>", From("The \"Orbit\" app"));
        Assert.Equal("\"C:\\\\Orbit\" <orbit@example.com>", From("C:\\Orbit"));
        Assert.Equal("Orbit Tracker <orbit@example.com>", From("Orbit\r\nTracker"));
    }

    /// <summary>MSG-026: a refusal reads as Resend's error name and message, or the start of whatever came back; an accepted email's id is read for the log.</summary>
    [Fact]
    public void Refusals_are_described_and_message_ids_read()
    {
        // As Resend answered a made-up key, and no key at all.
        Assert.Equal("Resend refused the email (401 validation_error): API key is invalid",
            ResendRules.Describe(401, """{"message":"API key is invalid","name":"validation_error","statusCode":401}"""));
        Assert.Equal("Resend refused the email (401 missing_api_key): Missing API Key",
            ResendRules.Describe(401, """{"message":"Missing API Key","name":"missing_api_key","statusCode":401}"""));
        Assert.Equal("Resend refused the email (403 validation_error): The example.com domain is not verified.",
            ResendRules.Describe(403, """{"statusCode":403,"name":"validation_error","message":" The example.com domain is not verified. "}"""));
        Assert.Equal("Resend is limiting how fast Orbit sends (429 daily_quota_exceeded): You have reached your daily email sending quota.",
            ResendRules.Describe(429, """{"statusCode":429,"name":"daily_quota_exceeded","message":"You have reached your daily email sending quota."}"""));
        Assert.Equal("Resend refused the email (422 missing_required_field).", ResendRules.Describe(422, """{"name":"missing_required_field"}"""));
        Assert.Equal("Resend is limiting how fast Orbit sends (429).", ResendRules.Describe(429, ""));
        Assert.Equal("Resend had a problem with the email (503): <html>Service unavailable</html>", ResendRules.Describe(503, " <html>Service unavailable</html> "));
        Assert.Equal("Resend refused the email (403): error code: 1010", ResendRules.Describe(403, "error code: 1010"));
        Assert.Equal("Resend refused the email (403).", ResendRules.Describe(403, null));

        var clipped = ResendRules.Describe(500, new string('x', 1000));
        Assert.EndsWith("…", clipped);
        Assert.True(clipped.Length < EmailApiRules.ErrorBodyMaxLength + 60);

        Assert.Equal("49a3999c-0ce1-4ea6-ab68-afcd6dc2e794", ResendRules.MessageId("""{"id":"49a3999c-0ce1-4ea6-ab68-afcd6dc2e794"}"""));
        Assert.Null(ResendRules.MessageId(""));
        Assert.Null(ResendRules.MessageId("not json"));
        Assert.Null(ResendRules.MessageId("""{"other":1}"""));
        Assert.Null(ResendRules.MessageId("""{"id":12}"""));
    }
}
