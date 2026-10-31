namespace Orbit.Application;

/// <summary>Whole-number percentages for the bars and charts: the dashboard's (spec §6.9) and the Today page's load bar (§6.12).</summary>
public static class Percentage
{
    /// <summary>A whole-number percentage, rounded half away from zero and kept within 0 to 100; 0 of nothing is 0 (DSH-007).</summary>
    public static int Of(int part, int whole) =>
        whole <= 0 ? 0 : (int)Math.Clamp(Math.Round(part * 100.0 / whole, MidpointRounding.AwayFromZero), 0, 100);
}
