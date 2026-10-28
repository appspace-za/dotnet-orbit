using MudBlazor;

namespace Orbit.Components.Layout;

/// <summary>
/// Orbit's MudBlazor theme (spec §6): an indigo primary on a deep-indigo app bar, in light and dark. The pages still on Razor
/// Pages get the same colours through the <c>--orbit-*</c> variables in site.css, so the two kinds of page look alike; change
/// both together. The font is the system UI stack Bootstrap uses, with no web-font download for an on-premises server.
/// </summary>
public static class OrbitTheme
{
    private static readonly string[] Fonts = ["system-ui", "-apple-system", "Segoe UI", "Roboto", "Helvetica Neue", "Arial", "sans-serif"];

    public static readonly MudTheme Theme = Build();

    private static MudTheme Build()
    {
        var theme = new MudTheme
        {
            PaletteLight = new PaletteLight
            {
                Primary = "#4338ca",
                Secondary = "#64748b",
                Tertiary = "#0f766e",
                Info = "#0369a1",
                Success = "#15803d",
                Warning = "#b45309",
                Error = "#dc2626",
                Dark = "#1f2937",
                AppbarBackground = "#1e1b4b",
                AppbarText = "#ffffff",
                Background = "#f5f6fa",
                Surface = "#ffffff",
                DrawerBackground = "#ffffff",
                TextPrimary = "#1f2937",
                TextSecondary = "#6b7280",
                TableHover = "#eef0fb"
            },
            PaletteDark = new PaletteDark
            {
                Primary = "#818cf8",
                PrimaryContrastText = "#0f1020",
                Secondary = "#94a3b8",
                SecondaryContrastText = "#0f1020",
                Tertiary = "#2dd4bf",
                TertiaryContrastText = "#0f1020",
                Info = "#38bdf8",
                InfoContrastText = "#0f1020",
                Success = "#4ade80",
                SuccessContrastText = "#0f1020",
                Warning = "#fbbf24",
                WarningContrastText = "#0f1020",
                Error = "#f87171",
                ErrorContrastText = "#0f1020",
                AppbarBackground = "#16172a",
                AppbarText = "#e5e7eb",
                Background = "#111218",
                Surface = "#1a1c25",
                DrawerBackground = "#1a1c25",
                TextPrimary = "#e5e7eb",
                TextSecondary = "#9ca3af",
                TableHover = "#232637"
            },
            LayoutProperties = new LayoutProperties { DefaultBorderRadius = "6px" }
        };

        var t = theme.Typography;
        foreach (var style in new[] { t.Default, t.H1, t.H2, t.H3, t.H4, t.H5, t.H6, t.Subtitle1, t.Subtitle2, t.Body1, t.Body2, t.Button, t.Caption, t.Overline })
            style.FontFamily = Fonts;
        t.Button.TextTransform = "none";
        return theme;
    }
}
