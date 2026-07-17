namespace CalDesk.App.Models;

public enum WindowMode
{
    Normal,
    Desktop,
    Topmost,
}

/// <summary>User-tunable application settings, persisted as JSON in %AppData%\CalDesk.</summary>
public sealed class AppSettings
{
    public WindowMode WindowMode { get; set; } = WindowMode.Normal;

    /// <summary>Window opacity, 0.15–1.0.</summary>
    public double Opacity { get; set; } = 0.92;

    public bool LockedPosition { get; set; }

    public bool StartWithWindows { get; set; }

    /// <summary>When true, skip remote certificate validation (self-signed / internal CA servers).</summary>
    public bool AcceptInsecureTls { get; set; }

    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public string? ScreenDeviceName { get; set; }

    public int DefaultDurationMinutes { get; set; } = 60;
    public int DefaultReminderMinutes { get; set; } = 15;
    public int SyncIntervalMinutes { get; set; } = 5;

    public Guid? DefaultCalendarId { get; set; }

    public DayOfWeek WeekStart { get; set; } = DayOfWeek.Monday;
}
