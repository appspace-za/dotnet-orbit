namespace Orbit.Components;

/// <summary>
/// What the first, server-rendered response hands the interactive shell (App.razor to Routes.razor, cascaded to the layout).
/// <see cref="Theme"/> is the remembered "light" or "dark", or null when there's none yet. <see cref="RequestToken"/> is the
/// antiforgery token the Sign out form posts to the Identity logout page.
/// </summary>
public sealed record ShellContext(string? Theme, string? RequestToken)
{
    /// <summary>The cookie (and localStorage key) holding the theme, shared with the Razor layout's theme script.</summary>
    public const string ThemeCookie = "orbit-theme";
}
