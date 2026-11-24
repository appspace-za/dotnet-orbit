namespace Orbit.Data.Entities;

/// <summary>
/// A stretch of a person's unavailable time (spec §6.22): from <see cref="From"/> to <see cref="To"/>, both included, all day when
/// <see cref="MinutesPerDay"/> is null or that many minutes a day otherwise. Overlaps are allowed; the availability rule never takes
/// a day below nothing. The reason and note are shown only to the person and whoever manages their availability.
/// </summary>
public class UnavailableTime
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public int? MinutesPerDay { get; set; }
    public UnavailableReason Reason { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedById { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedById { get; set; }
}
