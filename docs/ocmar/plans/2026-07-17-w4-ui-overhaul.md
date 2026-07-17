# W4 UI Overhaul — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use ocmar-subagent-driven-development (recommended) or ocmar-executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Overhaul CalDesk WPF UI per spec `docs/ocmar/specs/2026-07-17-w4-ui-overhaul-design.md` — resizable/DPI-robust borderless windows, S/M/L font scaling, reworked week & month views, calendar management (hide + 12-color), QuickAdd restyle.

**Architecture:** Pure Core logic (CalendarViewService overflow/BuildDay + palette) is TDD in `tests/CalDesk.Tests` (net10.0). WPF/App work (XAML, windowing, settings VM) is build-verified + manual-criteria (Linux can't run WPF; user verifies on Windows). Per established workflow, tasks are implemented by `fixer-grok` and gated by `rev-ro-groker` + `rev-ro-cgpter` (≥9.5). Personal single-user app, **+08 Beijing, NO DST** — never touch DST/TZID/IcalMapper recurrence.

**Tech Stack:** .NET 10, WPF (net10.0-windows), CommunityToolkit.Mvvm ([ObservableProperty]/[RelayCommand]), H.NotifyIcon tray, Microsoft.Data.Sqlite, xUnit.

## Global Constraints

- **+08 personal single-user, NO DST.** Do NOT modify `CalendarViewService` DST/TZID logic, `IcalMapper` recurrence, or any timezone handling.
- Core libraries (Core/CalDav/Storage) are protected; only `CalendarViewService` (additive: `BuildDay`, overflow rule) + `MonthDay` (add field) + new `CalendarPalette` change in Core. All other changes in `CalDesk.App`.
- `dotnet build CalDesk.slnx -c Debug` must be 0 error (App builds on Linux via EnableWindowsTargeting). `dotnet test tests/CalDesk.Tests` must stay green (currently 112).
- No new commits unless orchestrator merges (per-task: record diff, do NOT commit/push).
- FontSizes use **DynamicResource** (live S/M/L switch). Calendar colors stored as `#rrggbb`.

## File Structure

**Core (testable):**
- `src/CalDesk.Core/Models/MonthView.cs` — add `MonthDay.OverflowCount`.
- `src/CalDesk.Core/Services/CalendarViewService.cs` — overflow rule in `BuildMonth`; new `BuildDay`.
- `src/CalDesk.Core/Services/CalendarPalette.cs` — NEW: 12 preset colors.

**App (build+manual):**
- `src/CalDesk.App/Themes/FontSizes.S.xaml` / `.M.xaml` / `.L.xaml` — NEW (7 Double resources each).
- `src/CalDesk.App/Themes/AppControls.xaml` — NEW (shared control styles + HeaderBrushWeek/Month).
- `src/CalDesk.App/Windowing/BorderlessResize.cs` — NEW (WM_NCHITTEST edge resize).
- `src/CalDesk.App/Windowing/DpiHandling.cs` — NEW (DIU persistence + crispness + conditional DPI hook).
- `src/CalDesk.App/Models/AppSettings.cs` — add FontSize, window-size fields, MonthSplitRatio.
- `src/CalDesk.App/App.xaml` / `App.xaml.cs` — merge font dict; ApplyFontSize; window-size persistence.
- `src/CalDesk.App/Views/WeekWindow.xaml(.cs)`, `MonthWindow.xaml(.cs)`, `QuickAddWindow.xaml(.cs)`, `SettingsWindow.xaml(.cs)`.
- `src/CalDesk.App/ViewModels/MonthViewModel.cs` (SelectedDate/SelectedDay/SelectDayCommand), `SettingsViewModel.cs` (calendar mgmt + font).

---

### Task 1: Core view logic — overflow +n, BuildDay, palette  [TDD]

**Files:**
- Modify: `src/CalDesk.Core/Models/MonthView.cs` (MonthDay — add OverflowCount)
- Modify: `src/CalDesk.Core/Services/CalendarViewService.cs` (BuildMonth overflow; new BuildDay)
- Create: `src/CalDesk.Core/Services/CalendarPalette.cs`
- Test: `tests/CalDesk.Tests/CalendarViewServiceTests.cs` (add tests) + new `tests/CalDesk.Tests/CalendarPaletteTests.cs`

**Interfaces:**
- Consumes: existing `BucketByDay`, `WeekdayNames`, `MonthDay`, `WeekDay`, `OccurrenceItem`.
- Produces: `MonthDay.OverflowCount` (int, init, default 0); `CalendarViewService.BuildDay(events, calendars, date, tz, today, nowUtc) → WeekDay`; `CalendarPalette.Presets` (`IReadOnlyList<string>`, 12 hex).

**Acceptance Criteria:**
- `T1-C1`: `BuildMonth` with 4 events on one day → that day's `Preview.Count==2` AND `OverflowCount==2` (n=count-2).
- `T1-C2`: `BuildMonth` with ≤3 events on a day → `OverflowCount==0` and `Preview.Count==count` (3 events⇒3, 2⇒2).
- `T1-C3`: `BuildDay(date)` returns a `WeekDay` whose `AllDay`+`Timed` match the events on that day, `Header` like "周一 13", `IsToday` true when date==today.
- `T1-C4`: `CalendarPalette.Presets.Count==12`; every entry matches `^#[0-9a-f]{6}$` (lowercase); does NOT contain `#d9d9d9`.
- `T1-C5`: `dotnet test tests/CalDesk.Tests` green (existing 112 + new).

- [ ] **Step 1: Write failing tests** — append to `tests/CalDesk.Tests/CalendarViewServiceTests.cs`:

```csharp
[Fact]
public void BuildMonth_MoreThanThree_ShowsTwoAndOverflowCount()
{
    var events = Enumerable.Range(0, 4)
        .Select(i => MakeTimedEvent(Monday.AddDays(1), 10 + i, "E" + i)).ToArray();
    var svc = new CalendarViewService(new RecurrenceExpander());
    var view = svc.BuildMonth(events, CalMap(events), 2026, 7, Tz, Today, Now);
    var cell = view.Weeks.SelectMany(w => w.Days).First(d => d.OccurrenceCount == 4);
    Assert.Equal(2, cell.Preview.Count);
    Assert.Equal(2, cell.OverflowCount); // 4 - 2
}

[Fact]
public void BuildMonth_ThreeOrFewer_NoOverflow()
{
    var events = new[] { MakeTimedEvent(Monday, 9, "A"), MakeTimedEvent(Monday, 10, "B"), MakeTimedEvent(Monday, 11, "C") };
    var svc = new CalendarViewService(new RecurrenceExpander());
    var view = svc.BuildMonth(events, CalMap(events), 2026, 7, Tz, Today, Now);
    var cell = view.Weeks.SelectMany(w => w.Days).First(d => d.Date == Monday);
    Assert.Equal(0, cell.OverflowCount);
    Assert.Equal(3, cell.Preview.Count);
}

[Fact]
public void BuildDay_ReturnsSingleDayGrouped()
{
    var events = new[] { MakeTimedEvent(Monday, 9, "Timed"), MakeAllDayEvent(Monday, "AllDay") };
    var svc = new CalendarViewService(new RecurrenceExpander());
    var day = svc.BuildDay(events, CalMap(events), Monday, Tz, Today, Now);
    Assert.Equal(Monday, day.Date);
    Assert.Single(day.AllDay);
    Assert.Single(day.Timed);
    Assert.Contains(day.Header, CalendarViewService.Headers); // header weekday is one of Mon..Sun
    Assert.True(day.IsToday); // Monday == Today in this fixture
}
```
Use the existing helpers in that file (`MakeTimedEvent`/`MakeAllDayEvent`/`CalMap`/`Monday`/`Tz`/`Today`/`Now`) — if a helper name differs, mirror the pattern from `BuildWeek` tests already present (lines ~34-60). If `MakeAllDayEvent` is absent, build a `CalendarEvent` with `IsAllDay=true` matching the existing all-day test pattern at lines ~153+.

- [ ] **Step 1b: Write palette test** — create `tests/CalDesk.Tests/CalendarPaletteTests.cs`:

```csharp
using System.Text.RegularExpressions;
using CalDesk.Core.Services;
using Xunit;

public class CalendarPaletteTests
{
    [Fact]
    public void Presets_AreTwelveValidHex_NoGray()
    {
        var hex = new Regex("^#[0-9a-f]{6}$");
        Assert.Equal(12, CalendarPalette.Presets.Count);
        Assert.All(CalendarPalette.Presets, c => Assert.Matches(hex, c));
        Assert.DoesNotContain("#d9d9d9", CalendarPalette.Presets);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail** — `dotnet test tests/CalDesk.Tests --nologo -v q` → FAIL (OverflowCount/BuildDay/CalendarPalette not defined).

- [ ] **Step 3: Implement** — `src/CalDesk.Core/Models/MonthView.cs`, add to `MonthDay` (after `OccurrenceCount`):

```csharp
/// <summary>Events beyond the visible preview (count &gt; 3 ⇒ count-2). 0 when ≤3.</summary>
public int OverflowCount { get; init; }
```

In `src/CalDesk.Core/Services/CalendarViewService.cs` `BuildMonth`, replace the `preview`/cell block:

```csharp
var count = ordered.Count;
var visible = count > 3 ? 2 : 3;
var preview = ordered
    .Take(visible)
    .Select(x => new MonthOccurrence
    {
        EventId = x.EventId,
        Summary = x.Summary,
        TimeLabel = x.TimeLabel,
        IsAllDay = x.IsAllDay,
        Color = x.Color,
    })
    .ToList();

row.Add(new MonthDay
{
    Date = date,
    Day = date.Day,
    IsInMonth = date.Month == month,
    IsToday = date == today,
    IsWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
    OccurrenceCount = ordered.Count,
    OverflowCount = count > 3 ? count - 2 : 0,
    Preview = preview,
});
```
Add `BuildDay` method (after `BuildMonth`):

```csharp
/// <summary>Build a single-day detail (all-day + timed) for the month-view bottom panel.</summary>
public WeekDay BuildDay(
    IEnumerable<CalendarEvent> events,
    IReadOnlyDictionary<Guid, CalendarInfo> calendars,
    DateOnly date,
    TimeZoneInfo displayTz,
    DateOnly today,
    DateTimeOffset nowUtc)
{
    var byDay = BucketByDay(events, calendars, date, date.AddDays(1), displayTz, nowUtc, hideFullyPastBefore: null);
    var bucket = byDay[date];
    var all = bucket.Where(x => x.IsAllDay).OrderBy(x => x.StartUtc).ToList();
    var timed = bucket.Where(x => !x.IsAllDay).OrderBy(x => x.StartUtc).ToList();
    var idx = ((int)date.DayOfWeek + 6) % 7;
    return new WeekDay
    {
        Date = date,
        Header = $"{WeekdayNames[idx]} {date.Day}",
        IsToday = date == today,
        AllDay = all,
        Timed = timed,
    };
}
```

Create `src/CalDesk.Core/Services/CalendarPalette.cs`:

```csharp
namespace CalDesk.Core.Services;

/// <summary>Curated 12-color preset for per-calendar coloring (no gray/black/white;
/// high-contrast on the light card). From the user-supplied ColorBrewer set, trimmed.</summary>
public static class CalendarPalette
{
    public static readonly IReadOnlyList<string> Presets =
    [
        "#e31a1c", // 红
        "#fb8072", // 珊瑚
        "#fb9a99", // 粉
        "#ff7f00", // 橙
        "#fdb462", // 琥珀
        "#b3de69", // 黄绿
        "#33a02c", // 绿
        "#8dd3c7", // 薄荷
        "#1f78b4", // 蓝
        "#80b1d3", // 钢蓝
        "#6a3d9a", // 紫
        "#bc80bd", // 紫红
    ];
}
```

- [ ] **Step 4: Run tests** — `dotnet test tests/CalDesk.Tests --nologo -v q` → PASS (all green).
- [ ] **Step 5: Record diff** — `git rev-parse HEAD` (baseline); `git diff --stat <baseline>`.

---

### Task 2: Font token system (S/M/L) + AppSettings  [build+manual]

**Files:**
- Create: `src/CalDesk.App/Themes/FontSizes.S.xaml`, `FontSizes.M.xaml`, `FontSizes.L.xaml`
- Modify: `src/CalDesk.App/App.xaml` (merge S default), `src/CalDesk.App/App.xaml.cs` (ApplyFontSize)
- Modify: `src/CalDesk.App/Models/AppSettings.cs` (FontSizeLevel enum + FontSize field)
- Modify: replace `FontSize="..."` literals in `WeekWindow.xaml`, `MonthWindow.xaml`, `QuickAddWindow.xaml`, `SettingsWindow.xaml`

**Interfaces:**
- Produces: DynamicResource keys `FontTiny/FontMicro/FontSmall/FontBody/FontBase/FontHeader/FontTitle`; `App.ApplyFontSize(FontSizeLevel)`; `AppSettings.FontSize` (`FontSizeLevel`).

**Token→literal map (apply this to every FontSize literal in all windows):**

| literal | token | | literal | token |
|---|---|---|---|---|
| 9 | FontTiny | | 12 | FontBody |
| 10 | FontMicro | | 13 | FontBase |
| 11 | FontSmall | | 15 | FontHeader |
| | | | 17 | FontTitle |

S/M/L values (Double): FontTiny 9/10/12, FontMicro 10/11/13, FontSmall 11/12/14, FontBody 12/13/15, FontBase 13/14/16, FontHeader 15/16/18, FontTitle 17/18/20.

**Acceptance Criteria:**
- `T2-C1`: Three `FontSizes.*.xaml` files exist, each with exactly the 7 keys above as `<sys:Double>`; `App.xaml` merges `FontSizes.S.xaml` by default.
- `T2-C2`: No remaining numeric `FontSize="..."` literal in the 4 windows (grep clean); all use `{DynamicResource Font...}`.
- `T2-C3`: `AppSettings.FontSizeLevel` enum exists (Small/Medium/Large); `AppSettings.FontSize` defaults Small; `ApplyFontSize(level)` swaps the merged dictionary (remove old, add new) — readable in `App.xaml.cs`.
- `T2-C4`: `dotnet build CalDesk.slnx -c Debug` → 0 errors.

- [ ] **Step 1: Create the three dictionaries.** Example `FontSizes.S.xaml` (M/L identical structure, values from table):

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:sys="clr-namespace:System;assembly=mscorlib">
    <sys:Double x:Key="FontTiny">9</sys:Double>
    <sys:Double x:Key="FontMicro">10</sys:Double>
    <sys:Double x:Key="FontSmall">11</sys:Double>
    <sys:Double x:Key="FontBody">12</sys:Double>
    <sys:Double x:Key="FontBase">13</sys:Double>
    <sys:Double x:Key="FontHeader">15</sys:Double>
    <sys:Double x:Key="FontTitle">17</sys:Double>
</ResourceDictionary>
```

- [ ] **Step 2: AppSettings** — add enum + field in `src/CalDesk.App/Models/AppSettings.cs`:

```csharp
public enum FontSizeLevel { Small, Medium, Large }
```
and in the class: `public FontSizeLevel FontSize { get; set; } = FontSizeLevel.Small;`
Also add (used by Task 3): `public double WindowWidth { get; set; } = double.NaN; public double WindowHeight { get; set; } = double.NaN; public double MonthWindowWidth { get; set; } = double.NaN; public double MonthWindowHeight { get; set; } = double.NaN; public double MonthSplitRatio { get; set; } = 0.6;`

- [ ] **Step 3: App.xaml** — add `<Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries><ResourceDictionary Source="Themes/FontSizes.S.xaml"/></ResourceDictionary.MergedDictionaries>...keep TrayIcon...</ResourceDictionary></Application.Resources>` (wrap existing TrayIcon inside the ResourceDictionary).

- [ ] **Step 4: ApplyFontSize** — in `App.xaml.cs`:

```csharp
public void ApplyFontSize(FontSizeLevel level)
{
    var src = level switch
    {
        FontSizeLevel.Medium => "Themes/FontSizes.M.xaml",
        FontSizeLevel.Large => "Themes/FontSizes.L.xaml",
        _ => "Themes/FontSizes.S.xaml",
    };
    var dict = Resources.MergedDictionaries;
    for (var i = dict.Count - 1; i >= 0; i--)
        if (dict[i].Source?.OriginalString.Contains("FontSizes.") == true)
            dict.RemoveAt(i);
    dict.Add(new ResourceDictionary { Source = new Uri(src, UriKind.Relative) });
}
```
Call `ApplyFontSize(_settings.Load().FontSize)` once during startup (where settings are loaded).

- [ ] **Step 5: Sweep FontSize literals.** In each of the 4 window XAML files, replace every `FontSize="<n>"` with `FontSize="{DynamicResource <token>}"` per the table. Also replace the Window-level `FontSize="13"` → `FontSize="{DynamicResource FontBase}"`.

- [ ] **Step 6: Build** — `dotnet build CalDesk.slnx -c Debug` → 0 errors; `rg -n 'FontSize="[0-9]' src/CalDesk.App/Views` → no matches.
- [ ] **Step 7: Record diff.**

---

### Task 3: Windowing — borderless resize + DPI robustness  [build+manual]

**Files:**
- Create: `src/CalDesk.App/Windowing/BorderlessResize.cs`, `src/CalDesk.App/Windowing/DpiHandling.cs`
- Modify: `src/CalDesk.App/Views/WeekWindow.xaml.cs`, `MonthWindow.xaml.cs` (attach on SourceInitialized)
- Modify: `src/CalDesk.App/Views/MonthWindow.xaml` (`ResizeMode=CanResize`; `MinWidth=380 MinHeight=520`), `WeekWindow.xaml` (`MinWidth=320 MinHeight=500`)

**Interfaces:** `BorderlessResize.Attach(Window, double resizeBorder=6)`; `DpiHandling.Attach(Window)`.

**Acceptance Criteria:**
- `T3-C1`: `BorderlessResize.Attach` hooks `WM_NCHITTEST (0x0084)` and returns HT* codes within the resize border on all 4 edges/corners (source readable in `BorderlessResize.cs`).
- `T3-C2`: Both windows call `BorderlessResize.Attach(this)` and `DpiHandling.Attach(this)` in `OnSourceInitialized`; MonthWindow `ResizeMode=CanResize`.
- `T3-C3`: Window root Borders set `TextOptions.TextFormattingMode=Display`, `UseLayoutRounding=True`, `SnapsToDevicePixels=True` (crispness).
- `T3-C4`: Window size persisted: `SizeChanged`→save `Width/Height` (DIU) to AppSettings; restore on open. (Persistence wiring readable in code-behind / App.)
- `T3-C5`: `dotnet build CalDesk.slnx -c Debug` → 0 errors.

- [ ] **Step 1: BorderlessResize.cs:**

```csharp
using System.Windows;
using System.Windows.Interop;

namespace CalDesk.App.Windowing;

/// <summary>Enables edge/corner resize on a borderless (WindowStyle=None) transparent WPF window
/// by handling WM_NCHITTEST.</summary>
public static class BorderlessResize
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                      HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    public static void Attach(Window w, double resizeBorder = 6)
    {
        var src = PresentationSource.FromVisual(w) as HwndSource
                  ?? HwndSource.FromHwnd(new WindowInteropHelper(w).EnsureHandle());
        if (src == null) return;
        src.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != WM_NCHITTEST) return IntPtr.Zero;
            var x = (short)(lParam.ToInt32() & 0xffff);
            var y = (short)((lParam.ToInt32() >> 16) & 0xffff);
            var p = w.PointFromScreen(new Point(x, y));
            var b = resizeBorder * (PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0);
            bool left = p.X <= b, right = p.X >= w.ActualWidth - b;
            bool top = p.Y <= b, bottom = p.Y >= w.ActualHeight - b;
            int hit = 0;
            if (top && left) hit = HTTOPLEFT;
            else if (top && right) hit = HTTOPRIGHT;
            else if (bottom && left) hit = HTBOTTOMLEFT;
            else if (bottom && right) hit = HTBOTTOMRIGHT;
            else if (left) hit = HTLEFT;
            else if (right) hit = HTRIGHT;
            else if (top) hit = HTTOP;
            else if (bottom) hit = HTBOTTOM;
            if (hit != 0) { handled = true; return new IntPtr(hit); }
            return IntPtr.Zero;
        });
    }
}
```
(If a hook is already attached elsewhere, ensure no conflict — both windows currently only use `OnDrag`, no existing hit-test hook.)

- [ ] **Step 2: DpiHandling.cs** (diagnosis-first; explicit hook only if needed):

```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CalDesk.App.Windowing;

/// <summary>DPI robustness: (1) apply crispness flags, (2) persist DIU bounds,
/// (3) conditionally adopt WM_DPICHANGED suggested rect IF testing shows auto-handling fails
/// for transparent windows. Per spec §1.3 — do NOT hook unless manual test proves it's needed.</summary>
public static class DpiHandling
{
    private const int WM_DPICHANGED = 0x02E0;

    public static void Attach(Window w)
    {
        // (1) crispness
        w.UseLayoutRounding = true;
        w.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(w, TextRenderingMode.Auto);

        // (3) conditional DPI hook — guarded so it's a no-op unless enabled.
        // NOTE: WPF PerMonitorV2 normally auto-applies the suggested rect. Enable here only if
        // Windows dual-DPI testing shows the transparent window mis-sizes on monitor switch.
        if (!EnableExplicitDpiHook) return;
        var src = HwndSource.FromHwnd(new WindowInteropHelper(w).EnsureHandle());
        src?.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg != WM_DPICHANGED) return IntPtr.Zero;
            var rect = Marshal.PtrToStructure<NativeRect>(lp);
            var t = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice;
            double s = t?.M11 ?? 1.0;
            w.Left = rect.Left / s;
            w.Top = rect.Top / s;
            w.Width = (rect.Right - rect.Left) / s;
            w.Height = (rect.Bottom - rect.Top) / s;
            handled = true;
            return IntPtr.Zero;
        });
    }

    /// <summary>Flip to true ONLY after a dual-DPI test confirms auto-handling is insufficient.</summary>
    public static bool EnableExplicitDpiHook { get; set; } = false;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
}
```

- [ ] **Step 3: Wire + min sizes.** In `WeekWindow.xaml.cs` and `MonthWindow.xaml.cs` add:

```csharp
protected override void OnSourceInitialized(EventArgs e)
{
    base.OnSourceInitialized(e);
    Windowing.BorderlessResize.Attach(this);
    Windowing.DpiHandling.Attach(this);
}
```
In `MonthWindow.xaml`: `ResizeMode="CanResize"`, `MinWidth="380" MinHeight="520"`. In `WeekWindow.xaml`: `MinWidth="320" MinHeight="500"`.

- [ ] **Step 4: Size persistence (DIU).** In each window code-behind: on `Loaded`, restore `Width/Height` from AppSettings fields (Week uses WindowWidth/Height; Month uses MonthWindowWidth/Height) when finite; subscribe to `SizeChanged` → debounce/store into AppSettings + `SettingsService.Save` (mirror existing WindowLeft/Top persistence pattern in these files). Keep it minimal and consistent with the existing position-save code.

- [ ] **Step 5: Build** — `dotnet build CalDesk.slnx -c Debug` → 0 errors.
- [ ] **Step 6: Record diff.**

---

### Task 4: Week view — header bg, 今天 left, center title, per-day ＋, row bg, event color  [build+manual]

**Files:** Modify `src/CalDesk.App/Views/WeekWindow.xaml` only (VM unchanged — `AddNewCommand(DateOnly?)` already exists; `WeekDay.Date` exists).

**Acceptance Criteria:**
- `T4-C1`: Header is a 3-col grid: col0 `今天` (TodayCommand, GhostButton), col1 centered `‹ WeekHeader ›` (HorizontalAlignment=Center), col2 周|月 toggle. No global top-left ＋ remains in header.
- `T4-C2`: Header wrapped in a `Border` with `Background={StaticResource HeaderBrushWeek}` (#FFF1F3F9), CornerRadius=8, Padding, Margin below.
- `T4-C3`: Each day row's header line has a `＋` at the right with `Command=AddNewCommand CommandParameter={Binding Date}`.
- `T4-C4`: Each day `StackPanel` wrapped in a `Border` with row background: today `#143B82F6`, else `#0D000000` (weekend `#14000000`); CornerRadius=6, Margin=0,4.
- `T4-C5`: Event accent binds calendar color: all-day/timed event color comes from `{Binding Color}` (fallback default blue when null — use a converter or `TargetNullValue=#3B82F6`).
- `T4-C6`: `dotnet build CalDesk.slnx -c Debug` → 0 errors.

- [ ] **Step 1: Define shared brushes** in `App.xaml` ResourceDictionary: `<SolidColorBrush x:Key="HeaderBrushWeek" Color="#FFF1F3F9"/>` and `<SolidColorBrush x:Key="HeaderBrushMonth" Color="#FFF6F1F4"/>` (move to `Themes/AppControls.xaml` if Task 7 lands first — either location is fine, both merged in App).

- [ ] **Step 2: Rewrite header** (`WeekWindow.xaml` Grid.Row=0) to:

```xml
<Border Grid.Row="0" Background="{StaticResource HeaderBrushWeek}" CornerRadius="8" Padding="10,6" Margin="0,0,0,10">
  <Grid>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/>
    </Grid.ColumnDefinitions>
    <Button Grid.Column="0" Content="今天" Style="{StaticResource GhostButton}" Command="{Binding TodayCommand}"/>
    <StackPanel Grid.Column="1" Orientation="Horizontal" HorizontalAlignment="Center">
      <Button Content="‹" Style="{StaticResource GhostButton}" Command="{Binding PrevWeekCommand}" Margin="0,0,2,0"/>
      <TextBlock Text="{Binding WeekHeader}" FontSize="{DynamicResource FontHeader}" FontWeight="SemiBold" VerticalAlignment="Center" Margin="4,0"/>
      <Button Content="›" Style="{StaticResource GhostButton}" Command="{Binding NextWeekCommand}" Margin="2,0,0,0"/>
    </StackPanel>
    <Border Grid.Column="2" Background="#14000000" CornerRadius="7" Padding="2">
      <StackPanel Orientation="Horizontal">
        <Button Content="周" Style="{StaticResource ToggleSegmentActive}" IsEnabled="False"/>
        <Button Content="月" Style="{StaticResource ToggleSegment}" Click="OnMonth"/>
      </StackPanel>
    </Border>
  </Grid>
</Border>
```

- [ ] **Step 3: Per-day template** — wrap day `StackPanel` in a background `Border`; add ＋ to the date header line:

```xml
<DataTemplate>
  <Border CornerRadius="6" Padding="8,6" Margin="0,4">
    <Border.Style><Style TargetType="Border">
      <Setter Property="Background" Value="#0D000000"/>
      <Style.Triggers>
        <DataTrigger Binding="{Binding IsToday}" Value="True"><Setter Property="Background" Value="#143B82F6"/></DataTrigger>
      </Style.Triggers>
    </Style></Border.Style>
    <StackPanel>
      <Grid Margin="0,0,0,3">
        <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
        <StackPanel Orientation="Horizontal" Opacity="0.85">
          <TextBlock Text="{Binding Header}" FontWeight="SemiBold" FontSize="{DynamicResource FontBody}"/>
          <TextBlock Text=" 今天" FontSize="{DynamicResource FontTiny}" Foreground="#3B82F6" Margin="4,0,0,0"
                     Visibility="{Binding IsToday, Converter={StaticResource Bool2Vis}}"/>
        </StackPanel>
        <Button Grid.Column="1" Content="＋" FontSize="{DynamicResource FontTitle}" Style="{StaticResource GhostButton}"
                Command="{Binding DataContext.AddNewCommand, RelativeSource={RelativeSource AncestorType=Window}}"
                CommandParameter="{Binding Date}" ToolTip="为该日新增"/>
      </Grid>
      <!-- keep existing AllDay + Timed ItemsControls here, with FontSize→tokens and color binding -->
    </StackPanel>
  </Border>
</DataTemplate>
```

- [ ] **Step 4: Event color binding** — in the AllDay/Timed event templates, bind the accent to calendar color. For the timed TimeLabel keep `Foreground=#3B82F6`; add a 5px color bar before the summary: `<Border Width="5" ... Background="{Binding Color, TargetNullValue=#3B82F6}"/>` (mirror the month-cell color dot already in MonthWindow). For all-day, set the event Border `Background` to a tinted calendar color via `{Binding Color, TargetNullValue=#223B82F6}` (or keep the existing blue tint if simpler — but at minimum the timed row gets a color dot).

- [ ] **Step 5: Remove** the old bottom `今天` button (now in header); keep status TextBlock + ResizeGrip in Row 2.
- [ ] **Step 6: Build** → 0 errors.
- [ ] **Step 7: Record diff.**

---

### Task 5: Month view — split, splitter, equal rows, overflow, selected-day detail, cell bg  [build+manual]

**Files:** Modify `src/CalDesk.App/Views/MonthWindow.xaml`, `src/CalDesk.App/ViewModels/MonthViewModel.cs`.

**Interfaces:** `MonthViewModel.SelectedDate` (DateOnly?), `SelectedDay` (WeekDay?), `SelectDayCommand(DateOnly)`; uses `CalendarViewService.BuildDay` (Task 1).

**Acceptance Criteria:**
- `T5-C1`: MonthWindow Grid = header / weekday-row / top-month(*) / GridSplitter / bottom-day(*) / status; top:bottom default star ratio 3:2; both with MinHeight (top≥180, bottom≥140).
- `T5-C2`: Month grid renders 6 equal-height rows (UniformGrid Rows=6 or Grid 6×*); each cell MinHeight≥92.
- `T5-C3`: Cell shows `Preview`; when `OverflowCount>0`, a 3rd row `(+{OverflowCount})` appears and clicking it or the cell invokes `SelectDayCommand(Date)`.
- `T5-C4`: Clicking a cell sets `SelectedDate` and builds `SelectedDay` via `_viewService.BuildDay(...)`; bottom panel renders all-day+timed (reuse week single-day template) inside its own ScrollViewer; empty state "该日无事项".
- `T5-C5`: Bottom day-detail has a per-day ＋ (`AddNewCommand` with `CommandParameter=SelectedDate`).
- `T5-C6`: Cell/title backgrounds (today #143B82F6; in-month #0A000000; weekend day-number #B91C1C; header band `HeaderBrushMonth`).
- `T5-C7`: GridSplitter drag updates `MonthSplitRatio` (persisted via Task 3 AppSettings pattern); restored on load.
- `T5-C8`: `dotnet build CalDesk.slnx -c Debug` → 0 errors.

- [ ] **Step 1: MonthViewModel** — add fields/commands:

```csharp
[ObservableProperty] private DateOnly? _selectedDate;
[ObservableProperty] private WeekDay? _selectedDay;

public double SplitRatio { get; set; } = 0.6; // set from AppSettings on construct

[RelayCommand]
private async Task SelectDay(DateOnly date)
{
    SelectedDate = date;
    var tz = TimeZoneInfo.Local;
    var rangeStart = LocalMidnightToUtc(date, tz);
    var rangeEnd = LocalMidnightToUtc(date.AddDays(1), tz);
    var allCals = await _store.Calendars.GetAllAsync();
    var calMap = allCals.ToDictionary(c => c.Id);
    var selectedIds = allCals.Where(c => c.IsSelected).Select(c => c.Id).ToArray();
    var stored = await _store.Events.GetInRangeAsync(selectedIds, rangeStart, rangeEnd);
    SelectedDay = _viewService.BuildDay(stored, calMap, date, tz, DateOnly.FromDateTime(DateTime.Today), DateTimeOffset.UtcNow);
}
```
Add `using CalDesk.Core.Models;` if missing (WeekDay lives there). Default-select today on first load (call SelectDay after initial LoadAsync commits Month).

- [ ] **Step 2: MonthWindow.xaml layout** — restructure the outer Grid RowDefinitions to:

```xml
<RowDefinition Height="Auto"/>   <!-- header -->
<RowDefinition Height="Auto"/>   <!-- weekday headers -->
<RowDefinition Height="3*"/>     <!-- top month grid -->
<RowDefinition Height="Auto"/>   <!-- GridSplitter -->
<RowDefinition Height="2*"/>     <!-- bottom day detail -->
<RowDefinition Height="Auto"/>   <!-- status -->
```
Wrap header in `<Border Background="{StaticResource HeaderBrushMonth}" CornerRadius="8" Padding="10,6" Margin="0,0,0,8">…existing header…</Border>`.

- [ ] **Step 3: Top month grid** — change the weeks `ItemsControl` panel to a 6-row equal grid:

```xml
<ItemsControl Grid.Row="2" ItemsSource="{Binding Month.Weeks}">
  <ItemsControl.ItemsPanel>
    <ItemsPanelTemplate><UniformGrid Rows="6" Columns="1"/></ItemsPanelTemplate>
  </ItemsControl.ItemsPanel>
  <!-- existing per-week ItemsControl (UniformGrid Columns=7) as ItemTemplate, but cell MinHeight=92 -->
</ItemsControl>
```
Set each cell `Border` `MinHeight="92"`.

- [ ] **Step 4: Cell template + overflow** — after the `Preview` ItemsControl, add:

```xml
<Button Style="{StaticResource RowButton}" Margin="0"
        Visibility="{Binding IsEmpty, Converter={StaticResource Bool2Vis}, ConverterParameter=inverse}"
        Command="{Binding DataContext.SelectDayCommand, RelativeSource={RelativeSource AncestorType=Window}}"
        CommandParameter="{Binding Date}">
  <!-- (overflow row shown only when OverflowCount>0) -->
</Button>
<TextBlock Text="{Binding OverflowCount, StringFormat=+{0}}" FontSize="{DynamicResource FontTiny}"
           Foreground="#6B7280" Margin="2,0,0,0"
           Visibility="{Binding HasOverflow, Converter={StaticResource Bool2Vis}}"/>
```
Add a computed `HasOverflow => OverflowCount > 0` property on `MonthDay` (Core model — add `public bool HasOverflow => OverflowCount > 0;` next to `IsEmpty`). Wire the whole cell's click (empty area) to `SelectDayCommand(Date)` via the existing `OnCellAdd` → change to call SelectDay instead of add. (Per-day ＋ is in the bottom panel, not the cell.)
Make the cell Background a Style: in-month `#0A000000`, today `#143B82F6` (DataTrigger), spillover Opacity 0.35 (keep). Weekend day-number `Foreground=#B91C1C` via DataTrigger on `IsWeekend`.

- [ ] **Step 5: GridSplitter:**

```xml
<GridSplitter Grid.Row="3" Height="6" HorizontalAlignment="Stretch" Background="#11000000" ResizeBehavior="PreviousAndNext"
              DragCompleted="OnSplitChanged"/>
```
Code-behind `OnSplitChanged`: compute top-row star ratio from `ActualHeight` and store into `MonthViewModel.SplitRatio` + persist (AppSettings.MonthSplitRatio). On Loaded, set row star from persisted ratio.

- [ ] **Step 6: Bottom day-detail** (Grid.Row=4):

```xml
<ScrollViewer Grid.Row="4" VerticalScrollBarVisibility="Auto">
  <StackPanel Margin="0,4,0,0">
    <Grid Margin="0,0,0,4">
      <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
      <TextBlock Text="{Binding SelectedDay.Header, FallbackValue=该日无事项}" FontWeight="SemiBold" FontSize="{DynamicResource FontBody}"/>
      <Button Grid.Column="1" Content="＋" FontSize="{DynamicResource FontTitle}" Style="{StaticResource GhostButton}"
              Command="{Binding AddNewCommand}" CommandParameter="{Binding SelectedDate}" Visibility="{Binding SelectedDay, Converter={StaticResource Null2Vis}}"/>
    </Grid>
    <ContentControl Content="{Binding SelectedDay}" ContentTemplate="{StaticResource DayDetailTemplate}"/>
  </StackPanel>
</ScrollViewer>
```
Define `DayDetailTemplate` (DataTemplate, DataType=WeekDay) reusing the week single-day all-day+timed rendering (copy from WeekWindow's day inner template, with color binding). Add a `Null2Vis`/invert converter if not present (or reuse Bool2Vis with a VM `HasSelectedDay` bool). Keep minimal — a simple fallback "该日无事项" when SelectedDay==null suffices.

- [ ] **Step 7: Build** → 0 errors.
- [ ] **Step 8: Record diff.**

---

### Task 6: Calendar management — hide + 12-color  [build+manual]

**Files:** Modify `src/CalDesk.App/Views/SettingsWindow.xaml`, `src/CalDesk.App/ViewModels/SettingsViewModel.cs`; locate discovery-upsert call site (preserve user Color/IsSelected).

**Interfaces:** reuses `ICalendarRepository.UpsertAsync` (already persists Color+IsSelected) and `CalendarPalette.Presets` (Task 1).

**Acceptance Criteria:**
- `T6-C1`: Settings CalDAV section has a "日历管理" panel listing all calendars (DisplayName + account), each with a visibility CheckBox bound to a VM `CalendarRow.IsSelected` and a 12-swatch color picker (selected swatch highlighted).
- `T6-C2`: Saving calls `_store.Calendars.UpsertAsync(cal)` for each changed calendar (Color + IsSelected persisted); a refresh/reload follows.
- `T6-C3`: Discovery path does NOT overwrite a user-set Color or IsSelected (only backfills when missing) — code change at the discovered-calendar upsert site.
- `T6-C4`: Color flows to indicators: week timed/all-day events show the calendar color (via Task 4 binding); month cells already use `Color`.
- `T6-C5`: Hidden calendars (IsSelected=false) excluded from week/month (already filtered in VMs by `c.IsSelected` — verify unchanged).
- `T6-C6`: `dotnet build CalDesk.slnx -c Debug` → 0 errors; `dotnet test tests/CalDesk.Tests` green.

- [ ] **Step 1: VM** — in `SettingsViewModel`, add `ObservableCollection<CalendarRow> CalendarRows`; populate in `InitializeAsync` (map `_store.Calendars.GetAllAsync()` → rows). `CalendarRow` (nested class) holds `CalendarInfo` ref + `IsSelected`/`Color` props; a `SaveCalendarsCommand` iterates and `UpsertAsync`s changed ones, then `RefreshCalendarsAsync`/reload.

- [ ] **Step 2: XAML** — add below the CalDAV account section:

```xml
<GroupBox Header="日历管理" Margin="0,8,0,0">
  <ItemsControl ItemsSource="{Binding CalendarRows}">
    <ItemsControl.ItemTemplate>
      <DataTemplate>
        <Grid Margin="0,4">
          <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
          <CheckBox Grid.Column="0" Content="{Binding DisplayName}" IsChecked="{Binding IsSelected}"/>
          <ItemsControl Grid.Column="1" ItemsSource="{x:Static core:CalendarPalette.Presets}" Margin="8,0,0,0">
            <ItemsControl.ItemTemplate>
              <DataTemplate>
                <Border Width="16" Height="16" CornerRadius="3" Margin="2,0" Background="{Binding}"
                        MouseLeftButtonDown="OnPickColor"/>
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
        </Grid>
      </DataTemplate>
    </ItemsControl.ItemTemplate>
  </ItemsControl>
</GroupBox>
```
`OnPickColor` code-behind: set the row's `Color` to the clicked swatch; mark a selected ring (e.g. border thickness) when `row.Color == swatch`. (Add `xmlns:core` to the window.) A "保存日历" button binds to `SaveCalendarsCommand`.

- [ ] **Step 3: Discovery preserve** — find where discovered calendars are upserted (search `_store.Calendars.UpsertAsync` / sync engine calendar merge). Before upserting a discovered `CalendarInfo`, load the existing one (`GetAsync(id)`); if existing has a non-null `Color` or `IsSelected==false`, preserve those fields (don't overwrite with server values). Keep server Color only when existing.Color is null.

- [ ] **Step 4: Font setting** — also add the S/M/L selector here (or in a Display section): three radio buttons bound to `AppSettings.FontSize` (FontSizeLevel), on change call `App.ApplyFontSize(level)` + save.
- [ ] **Step 5: Build + test** → 0 errors; tests green.
- [ ] **Step 6: Record diff.**

---

### Task 7: QuickAdd restyle + AppControls.xaml  [build+manual]

**Files:** Create `src/CalDesk.App/Themes/AppControls.xaml`; Modify `src/CalDesk.App/Views/QuickAddWindow.xaml(.cs)`.

**Acceptance Criteria:**
- `T7-C1`: `QuickAddWindow` is `WindowStyle=None AllowsTransparency=True Background=Transparent`; root Border `#F4FFFFFF` CornerRadius=10 Padding=16 + DropShadow (matches main windows).
- `T7-C2`: Custom title/drag area (`MouseLeftButtonDown` → `DragMove`) + top-right `×` close (IsCancel). ESC/Enter still work.
- `T7-C3`: `AppControls.xaml` defines shared TextBox/ComboBox/DatePicker/CheckBox/Button styles (token font sizes, rounded borders); merged in App.xaml; used by QuickAdd (and optionally other windows).
- `T7-C4`: `TextOptions.TextFormattingMode=Display` set (DPI crispness).
- `T7-C5`: `dotnet build CalDesk.slnx -c Debug` → 0 errors.

- [ ] **Step 1: AppControls.xaml** — define `Styles/ControlStyle` keys for TextBox/ComboBox/DatePicker/Button (CornerRadius via ControlTemplate where needed; FontSize={DynamicResource FontBody}). Merge in `App.xaml` MergedDictionaries.
- [ ] **Step 2: QuickAddWindow.xaml** — change Window attributes; wrap content in the card Border; add title row with drag area + close button. Move the `[ 更多选项 ]` / error / bottom bar inside the card. Apply AppControls styles.
- [ ] **Step 3: code-behind** — add `OnDrag` (DragMove) + `OnClose` (Close). Keep `SizeToContent=Height`, `ResizeMode=NoResize`. Attach `DpiHandling` crispness (or set TextOptions in XAML).
- [ ] **Step 4: Build** → 0 errors.
- [ ] **Step 5: Record diff.**

---

### Task 8: Integration verify + gate  [final]

**Files:** none (verification).

**Acceptance Criteria:**
- `T8-C1`: `dotnet build CalDesk.slnx -c Debug` → 0 errors; `dotnet test tests/CalDesk.Tests --nologo` → all green.
- `T8-C2`: Gate `rev-ro-groker` + `rev-ro-cgpter` both ≥9.5 (note: +08/no-DST, WPF UI overhaul; Core changes are T1).
- `T8-C3`: After gate PASS → `git push origin main` + winbuild `publish.ps1`.
- `T8-C4` (final-only, manual on Windows): resizable both windows (edges+corners); DPI dual-monitor OK; S/M/L switch; week 今天-left/per-day +/center; month split+overflow+selected-day; calendar hide/color; QuickAdd restyle.

- [ ] **Step 1: Build+test** → record pass/fail.
- [ ] **Step 2: Dispatch gate** (both reviewers) on integrated main with spec + plan as context.
- [ ] **Step 3: On PASS → push + publish; on FAIL → fixer-grok fixes per findings → re-gate.

---

## Criterion Ownership Matrix

| Criterion | Spec item | Owner | Cross-deps | Verification | Final-only |
|---|---|---|---|---|---|
| T1-C1..C5 | §3.3 overflow, §3.2 BuildDay, §4 palette | T1 | — | `dotnet test` (CalendarViewServiceTests/CalendarPaletteTests) | N |
| T2-C1..C4 | §1.1 (item 10) | T2 | — | build + `rg -n 'FontSize="[0-9]' src/CalDesk.App/Views` clean | N |
| T3-C1..C5 | §1.2, §1.3 (items 1, 9) | T3 | — | build + source inspection | N |
| T4-C1..C6 | §2.1, §2.2, §2.3 (items 1-hdr, 2, 3-wk) | T4 | T2 (tokens) | build + source | N |
| T5-C1..C8 | §3.1..§3.4 (items 4, 5, 8, 3-mo, 1-mo-hdr) | T5 | T1, T2 | build + source | N |
| T6-C1..C6 | §4, §1.4 (item 6) | T6 | T1 (palette), T4 (color binding) | build + test | N |
| T7-C1..C5 | §5 (item 7) | T7 | T2 (tokens) | build | N |
| T8-C1 | all | T8 | T1..T7 | build+test | N |
| T8-C2 | all | T8 | T1..T7 | gate scores | N |
| T8-C4 | items 1-10 | T8 | all | manual Windows | **Y** |
| DPI dual-monitor correctness | item 9 | T8 | T3 | manual dual-DPI | **Y** |
| Live S/M/L switch | item 10 | T8 | T2 | manual | **Y** |

## Self-Review (run before execution)
- **Spec coverage:** items 1(T3/T4/T5), 2(T4), 3(T4/T5), 4(T5), 5(T5/T1), 6(T6/T1), 7(T7), 8(T5/T3), 9(T3), 10(T2) — all owned. ✓
- **Type consistency:** `OverflowCount` (T1) used in T5; `BuildDay(events,calendars,date,tz,today,nowUtc)` (T1) called in T5 with matching args; `CalendarPalette.Presets` (T1) used in T6; `ApplyFontSize(FontSizeLevel)` (T2) called in T6; `BorderlessResize.Attach`/`DpiHandling.Attach` (T3) used in T4/T5/T7 code-behind. ✓
- **Placeholders:** none — every code step has concrete code; XAML edits show the changed blocks. ✓
- **Observability:** Core tasks have unit tests; App tasks have build + grep/source-inspection criteria; visual criteria marked final-only/manual. ✓
