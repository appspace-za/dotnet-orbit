using Orbit.Application.Models;

namespace Orbit.Data.Entities;

/// <summary>
/// A person's work pattern (spec §6.22): the minutes they are expected to work each weekday, in effect from <see cref="EffectiveFrom"/>
/// until their next pattern. At most one per person per date; the first covers the dates before it. Availability is worked out from
/// these, the organisation's holidays and <see cref="UnavailableTime"/>, never stored per day.
/// </summary>
public class WorkPattern
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public DateOnly EffectiveFrom { get; set; }
    public int MondayMinutes { get; set; }
    public int TuesdayMinutes { get; set; }
    public int WednesdayMinutes { get; set; }
    public int ThursdayMinutes { get; set; }
    public int FridayMinutes { get; set; }
    public int SaturdayMinutes { get; set; }
    public int SundayMinutes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedById { get; set; }

    /// <summary>The seven days as one value; not mapped.</summary>
    public WeekMinutes Week
    {
        get => new(MondayMinutes, TuesdayMinutes, WednesdayMinutes, ThursdayMinutes, FridayMinutes, SaturdayMinutes, SundayMinutes);
        set
        {
            MondayMinutes = value.Monday;
            TuesdayMinutes = value.Tuesday;
            WednesdayMinutes = value.Wednesday;
            ThursdayMinutes = value.Thursday;
            FridayMinutes = value.Friday;
            SaturdayMinutes = value.Saturday;
            SundayMinutes = value.Sunday;
        }
    }
}
