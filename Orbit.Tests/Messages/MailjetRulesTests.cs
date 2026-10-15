using System.Text;
using System.Text.Json;
using Orbit.Application;
using Orbit.Application.Messages;

namespace Orbit.Tests.Messages;

/// <summary>Sending through Mailjet's Send API v3.1 (spec §6.7).</summary>
public class MailjetRulesTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private const string Secret = "fedcba9876543210fedcba9876543210";

    private static EmailOptions Settings(Action<EmailOptions>? change = null)
    {
        var options = new EmailOptions
        {
            Provider = EmailProvider.Mailjet,
            FromAddress = "orbit@example.com",
            FromName = "Orbit",
            Mailjet = new MailjetOptions { ApiKey = Key, SecretKey = Secret }
        };
        change?.Invoke(options);
        return options;
    }

    /// <summary>MSG-021: settings that will send pass; each that won't is named by its key; the credentials are Basic's key:secret.</summary>
    [Fact]
    public void Config_problems_name_what_is_missing_or_wrong()
    {
        Assert.Empty(MailjetRules.ConfigProblems(Settings()));
        Assert.Empty(MailjetRules.ConfigProblems(Settings(o => o.FromName = "")));
        Assert.Empty(MailjetRules.ConfigProblems(Settings(o => o.FromName = new string('x', 71))));
        Assert.Empty(MailjetRules.ConfigProblems(Settings(o => o.Mailjet.BaseUrl = "http://127.0.0.1:5199/v3.1")));

        Assert.Contains("Email:Mailjet:ApiKey", Assert.Single(MailjetRules.ConfigProblems(Settings(o => o.Mailjet.ApiKey = " "))));
        Assert.Contains("Email:Mailjet:SecretKey", Assert.Single(MailjetRules.ConfigProblems(Settings(o => o.Mailjet.SecretKey = null))));
        Assert.Contains("Email:FromAddress is not set", Assert.Single(MailjetRules.ConfigProblems(Settings(o => o.FromAddress = null))));
        Assert.Contains("not an email address", Assert.Single(MailjetRules.ConfigProblems(Settings(o => o.FromAddress = "Orbit <orbit@example.com>"))));
        Assert.Contains("Email:Mailjet:BaseUrl", Assert.Single(MailjetRules.ConfigProblems(Settings(o => o.Mailjet.BaseUrl = "http://api.mailjet.com/v3.1/"))));
        Assert.Contains("Email:Mailjet:BaseUrl", Assert.Single(MailjetRules.ConfigProblems(Settings(o => o.Mailjet.BaseUrl = "api.mailjet.com"))));
        Assert.Equal(3, MailjetRules.ConfigProblems(Settings(o => { o.Mailjet.ApiKey = null; o.Mailjet.SecretKey = ""; o.FromAddress = null; })).Count);

        Assert.Equal("https://api.mailjet.com/v3.1/send", new Uri(MailjetRules.BaseUri(new MailjetOptions()), MailjetRules.SendPath).ToString());
        Assert.Equal("https://api.us.mailjet.com/v3.1/send",
            new Uri(MailjetRules.BaseUri(new MailjetOptions { BaseUrl = " https://api.us.mailjet.com/v3.1 " }), MailjetRules.SendPath).ToString());

        var credentials = MailjetRules.Credentials(new MailjetOptions { ApiKey = $" {Key} ", SecretKey = $"{Secret}\n" });
        Assert.Equal($"{Key}:{Secret}", Encoding.UTF8.GetString(Convert.FromBase64String(credentials)));
    }

    /// <summary>MSG-022: one email is one Mailjet message - From, one To, Subject and HTMLPart, in its PascalCase names, with no empty fields and no keys.</summary>
    [Fact]
    public void Request_is_one_message_in_mailjets_names()
    {
        var json = JsonSerializer.Serialize(MailjetRules.Request(Settings(o => o.FromAddress = " orbit@example.com "), " ann@example.com ",
            "Task T-26-00012 assigned", "<p>Hi</p>\n"), MailjetRules.Json);
        Assert.DoesNotContain(Key, json);
        Assert.DoesNotContain(Secret, json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(["Messages"], root.EnumerateObject().Select(p => p.Name));
        var message = Assert.Single(root.GetProperty("Messages").EnumerateArray());
        Assert.Equal(["From", "To", "Subject", "HTMLPart"], message.EnumerateObject().Select(p => p.Name));
        var from = message.GetProperty("From");
        Assert.Equal(["Email", "Name"], from.EnumerateObject().Select(p => p.Name));
        Assert.Equal("orbit@example.com", from.GetProperty("Email").GetString());
        Assert.Equal("Orbit", from.GetProperty("Name").GetString());
        var to = Assert.Single(message.GetProperty("To").EnumerateArray());
        Assert.Equal(["Email"], to.EnumerateObject().Select(p => p.Name));
        Assert.Equal("ann@example.com", to.GetProperty("Email").GetString());
        Assert.Equal("Task T-26-00012 assigned", message.GetProperty("Subject").GetString());
        Assert.Equal("<p>Hi</p>\n", message.GetProperty("HTMLPart").GetString());

        var nameless = JsonSerializer.Serialize(MailjetRules.Request(Settings(o => o.FromName = " "), "ann@example.com", "S", "<p>B</p>"), MailjetRules.Json);
        using var namelessDocument = JsonDocument.Parse(nameless);
        Assert.Equal(["Email"], namelessDocument.RootElement.GetProperty("Messages")[0].GetProperty("From").EnumerateObject().Select(p => p.Name));
    }

    /// <summary>
    /// MSG-023: a refusal reads as Mailjet's code and message, from the top of its answer or from each message, or the start of whatever
    /// came back; a 2xx is accepted unless its message failed; an accepted email's id is read for the log.
    /// </summary>
    [Fact]
    public void Refusals_are_described_and_message_ids_read()
    {
        // What Mailjet answered a made-up key.
        Assert.Equal("Mailjet refused the email (401): API key authentication/authorization failure. You may be unauthorized to access the API " +
                     "or your API key may be expired. Visit API keys management section to check your keys.",
            MailjetRules.Describe(401, """{"ErrorIdentifier":"62786818-87cf-486b-b2dc-46d3af897bb8","StatusCode":401,"ErrorMessage":"API key authentication/authorization failure. You may be unauthorized to access the API or your API key may be expired. Visit API keys management section to check your keys."}"""));
        Assert.Equal("Mailjet refused the email (401): Not authorized", MailjetRules.Describe(401, "Not authorized"));
        Assert.Equal("Mailjet refused the email (400 mj-0002): Malformed JSON, please review the syntax and properties types.",
            MailjetRules.Describe(400, """{"ErrorIdentifier":"x","ErrorCode":"mj-0002","ErrorMessage":"Malformed JSON, please review the syntax and properties types.","StatusCode":400}"""));
        Assert.Equal("Mailjet refused the email (400 mj-0013): \"ann@\" is an invalid email address.",
            MailjetRules.Describe(400, """{"Messages":[{"Status":"error","Errors":[{"ErrorIdentifier":"x","ErrorCode":"mj-0013","StatusCode":400,"ErrorMessage":"\"ann@\" is an invalid email address.","ErrorRelatedTo":["To[0].Email"]}]}]}"""));
        Assert.Equal("Mailjet refused the email (403 send-0008): The From address isn't allowed; Subject is too long",
            MailjetRules.Describe(403, """{"Messages":[{"Status":"error","Errors":[{"ErrorCode":"send-0008","ErrorMessage":"The From address isn't allowed"},{"ErrorCode":"mj-0006","ErrorMessage":"Subject is too long"},{"ErrorMessage":"Subject is too long"}]}]}"""));
        Assert.Equal("Mailjet is limiting how fast Orbit sends (429).", MailjetRules.Describe(429, ""));
        Assert.Equal("Mailjet had a problem with the email (500): <html>Oops</html>", MailjetRules.Describe(500, " <html>Oops</html> "));

        var clipped = MailjetRules.Describe(502, new string('x', 1000));
        Assert.EndsWith("…", clipped);
        Assert.True(clipped.Length < EmailApiRules.ErrorBodyMaxLength + 60);

        const string accepted = """{"Messages":[{"Status":"success","CustomID":"","To":[{"Email":"ann@example.com","MessageUUID":"1ab23cd4-e567-8901-2345-6789f0gh1i2j","MessageID":1152921510012345678,"MessageHref":"https://api.mailjet.com/v3/REST/message/1152921510012345678"}],"Cc":[],"Bcc":[]}]}""";
        Assert.Null(MailjetRules.Refusal(200, accepted));
        Assert.Null(MailjetRules.Refusal(201, ""));
        Assert.Equal("Mailjet refused the email (200 mj-0013): bad", MailjetRules.Refusal(200,
            """{"Messages":[{"Status":"error","Errors":[{"ErrorCode":"mj-0013","ErrorMessage":"bad"}]}]}"""));
        Assert.Equal("Mailjet refused the email (401): Not authorized", MailjetRules.Refusal(401, "Not authorized"));

        Assert.Equal("1152921510012345678", MailjetRules.MessageId(accepted));
        Assert.Equal("1ab23cd4", MailjetRules.MessageId("""{"Messages":[{"Status":"success","To":[{"Email":"a@b.c","MessageUUID":"1ab23cd4"}]}]}"""));
        Assert.Equal("77", MailjetRules.MessageId("""{"Messages":[{"To":[{"MessageID":"77","MessageUUID":"u"}]}]}"""));
        Assert.Null(MailjetRules.MessageId(""));
        Assert.Null(MailjetRules.MessageId("not json"));
        Assert.Null(MailjetRules.MessageId("""{"Messages":[]}"""));
    }
}
