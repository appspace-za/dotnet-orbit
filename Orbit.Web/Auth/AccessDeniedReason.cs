using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Orbit.Application;

namespace Orbit.Auth;

/// <summary>
/// The reason the access denied page (<c>Areas/Identity/Pages/Account/AccessDenied</c>) shows: a refusal's own message, carried in
/// TempData across the cookie's redirect. It has a key of its own, apart from the flash messages, so a flash that was never shown
/// can't turn up later as the reason for an unrelated refusal (decision 108).
/// </summary>
public static class AccessDeniedReason
{
    public const string Key = "AccessDeniedReason";

    /// <summary>
    /// Answers a plain request that a service refused (a file endpoint, decision 110): the cookie's access denied page, showing the
    /// refusal's message. A minimal API has no MVC filter to save TempData, so it is saved here, before the redirect is written.
    /// </summary>
    public static IResult Forbid(HttpContext http, ForbiddenException refusal)
    {
        var tempData = http.RequestServices.GetRequiredService<ITempDataDictionaryFactory>().GetTempData(http);
        tempData[Key] = refusal.Message;
        tempData.Save();
        return Results.Forbid();
    }
}
