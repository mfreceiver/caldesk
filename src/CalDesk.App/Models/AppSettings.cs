namespace CalDesk.App.Models;

public enum WindowMode
{
    Normal,
    Desktop,
    Topmost,
}

/// <summary>Font size scaling tier (S/M/L) applied via DynamicResource font dictionaries.</summary>
public enum FontSizeLevel
{
    Small,
    Medium,
    Large,
}

/// <summary>Theme mode: follow OS, force light, or force dark. Default light (§3.7, decision 10).</summary>
public enum AppThemeMode
{
    FollowSystem,
    Light,
    Dark,
}

public enum ViewMode
{
    Week,
    Month,
}

/// <summary>User-tunable application settings, persisted as JSON in %AppData%\CalDesk.</summary>
public sealed class AppSettings
{
    public WindowMode WindowMode { get; set; } = WindowMode.Normal;

    public bool LockedPosition { get; set; }

    public bool StartWithWindows { get; set; }

    /// <summary>When true, skip remote certificate validation (self-signed / internal CA servers).</summary>
    public bool AcceptInsecureTls { get; set; }

    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;

    // T10/C1+D6: 月窗独立位置 (DIU)。原"月窗复用周窗位置"导致锁定场景下月窗被错误
    // 拉到周窗坐标, 退出后亦可能被周窗守卫污染. 月窗与周窗各持独立 Left/Top.
    public double MonthWindowLeft { get; set; } = double.NaN;
    public double MonthWindowTop { get; set; } = double.NaN;
    public string? ScreenDeviceName { get; set; }

    public int DefaultDurationMinutes { get; set; } = 60;
    public int DefaultReminderMinutes { get; set; } = 15;
    public int SyncIntervalMinutes { get; set; } = 5;

    public Guid? DefaultCalendarId { get; set; }

    /// <summary>Font size tier; swapped at runtime via <c>App.ApplyFontSize</c>.</summary>
    public FontSizeLevel FontSize { get; set; } = FontSizeLevel.Small;

    /// <summary>Theme mode; swapped at runtime via <c>App.ApplyTheme</c>. Default light.</summary>
    public AppThemeMode Theme { get; set; } = AppThemeMode.Light;

    public ViewMode LastViewMode { get; set; } = ViewMode.Week;

    // --- Window-size persistence (used by Task 3: borderless resize + DPI robustness) ---

    /// <summary>Persisted week-window width (DIU). NaN = use default size.</summary>
    public double WindowWidth { get; set; } = double.NaN;

    /// <summary>Persisted week-window height (DIU). NaN = use default size.</summary>
    public double WindowHeight { get; set; } = double.NaN;

    /// <summary>Persisted month-window width (DIU). NaN = use default size.</summary>
    public double MonthWindowWidth { get; set; } = double.NaN;

    /// <summary>Persisted month-window height (DIU). NaN = use default size.</summary>
    public double MonthWindowHeight { get; set; } = double.NaN;
}
