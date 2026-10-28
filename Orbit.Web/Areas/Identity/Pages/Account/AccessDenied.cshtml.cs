using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orbit.Auth;

namespace Orbit.Areas.Identity.Pages.Account;

/// <summary>The cookie's access denied page: the service's reason when a refused request set one, else the general text.</summary>
[AllowAnonymous]
public class AccessDeniedModel : PageModel
{
    public string? Reason { get; private set; }

    public void OnGet()
    {
        Reason = TempData[AccessDeniedReason.Key] as string;
    }
}
