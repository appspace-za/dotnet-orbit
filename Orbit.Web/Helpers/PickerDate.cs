using System.Globalization;

namespace Orbit.Helpers;

/// <summary>
/// MudDatePicker's value, a <see cref="DateTime"/> at midnight, against Orbit's days (<see cref="DateOnly"/>), and against a day
/// written "2026-10-28" in an address or a stored answer. One copy for every picker (decision 112).
/// </summary>
public static class PickerDate
{
    public const string Format = "yyyy-MM-dd";

    /// <summary>The picker's value for a day.</summary>
    public static DateTime? Of(DateOnly? day) => day?.ToDateTime(TimeOnly.MinValue);

    /// <summary>The day a picker's value stands for.</summary>
    public static DateOnly? Day(DateTime? value) => value is DateTime d ? DateOnly.FromDateTime(d) : null;

    /// <summary>The picker's value for a day written "2026-10-28", or null when there is none or it isn't one.</summary>
    public static DateTime? Parse(string? text) =>
        DateOnly.TryParseExact(text?.Trim(), Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? Of(day) : null;

    /// <summary>The day a picker's value stands for, written "2026-10-28".</summary>
    public static string? Write(DateTime? value) => Day(value)?.ToString(Format, CultureInfo.InvariantCulture);
}
