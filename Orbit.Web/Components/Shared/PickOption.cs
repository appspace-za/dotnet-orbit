namespace Orbit.Components.Shared;

/// <summary>
/// One choice in a <see cref="SearchSelect{TValue}"/> (spec §6.2): the value it stands for, the text the box shows, and an
/// optional second line under it in the list (a number, an email, a department) that a search matches too. A record, as the
/// autocomplete underneath needs value equality to show the full list while a choice is made.
/// </summary>
public sealed record PickOption<TValue>(TValue Value, string Text, string? Detail = null);
