# CalDesk UI 系统化改造 — 最终执行方案（v1.0）

**日期**：2026-07-20 ｜ **状态**：可派发 ｜ **基线**：rev-kimi 全量方案 + rev-glm/rev-gpt 三方对账裁决

本文件是**自包含**的执行 playbook。基线代码由 rev-kimi 产出，rev-glm 与 rev-gpt 独立评审，orchestrator 对三方分歧逐项裁决。**§3 的代码为最终权威**（覆盖任何他处冲突表述）。派发时各 lane 读本文件即可，无需访问临时文件。

源码根：`/home/mar/personal_projects/caldesk`（WPF net10.0-windows；Linux 仅编库+测试，WPF 须 `ssh winbuild`）。

---

## §1 锁定决策（11 条，不可推翻）

1. 全量调色板重构（~85 字面量集中）。
2. 重设调色板数值（非保值）。
3. 架构深色模式（浅/深成对 token + 运行时热切换，镜像 FontSize）。
4. `保存连接信息`保留"保存+发现+同步"；主`保存`纳入日历颜色本地 upsert + 刷新已开窗口，**不**触发服务器同步。
5. 月视图纯 Star 比例 + 底部 MaxHeight(10行)/MinHeight(3行)，**移除 GridSplitter**，分隔线静态发丝。
6. W8 = `M月第W周`，周四定月，`W=((thu.Day-1)/7)+1`。
7. S3 单选不联动清除，声明式 DataTrigger。
8. S3 视觉 = 2px accent 外环 + 1.5px 白内描边 + 中心白勾。
9. W2 当天 = accent 淡填 + 加粗 + 1px 边框。
10. 深色入口三选（跟随系统/浅/深），默认浅，持久化。
11. **M2 月格不显 TimeLabel**（只显色点+Summary+省略号）；TimeLabel 只在底部单日详情面板显。

---

## §2 三方对账裁决（分歧 → 终裁）

| # | 议题 | rev-kimi | rev-glm | rev-gpt | **终裁（采纳）** |
|---|---|---|---|---|---|
| R1 | `TargetNullValue=#3B82F6` 计数 | 4 处 | **5 处**（grep 实测） | 同意 5 | **5 处**：Week:161/182、Month:212/302/324。Lane-W 改 2、Lane-M 改 3（含 :212） |
| R2 | `ColorOrAccentConverter` 主题切换不刷新 | 🟡 可接受 | 🔴 广播 ThemeChangedMessage | **结构性正解**：`BrushResourceProxy:Freezable` | **采纳 rev-gpt**：BrushResourceProxy + MultiBinding（无需 ThemeChangedMessage，converter 随主题自动重算） |
| R3 | ScrollBar 模板 | 隐式全局，Track 缺绑定 | 同意改 keyed | **🔴 thumb 不会动**（Track 缺 Minimum/Maximum/Value/ViewportSize）+ 隐式污染弹出层 | **采纳 rev-gpt**：keyed `SlimScrollBar` + Track 全绑定；SlimScrollViewer 显式引用；native 弹出层零影响 |
| R4 | M3 行高方案 | 资源键推荐，ComputeBottomLimits 备选 | 反转：直接 ComputeBottomLimits | **🔴 高度冲突**：cell MinHeight=92×6=552 vs 窗口 408 | **采纳 rev-glm + rev-gpt 补强**：`ComputeBottomLimits()` 为主（确定性），**且 Row2.MinHeight = 6×cellMin**（非 180）；窗口 MinHeight ≈ 600（S 档，按 spec "每格 3 行+底部 3 行"） |
| R5 | SystemThemeWatcher 线程模型 | 注释称自动 marshal（**错**） | 修注释：SystemEvents 在 worker 线程 | 弱引用静态回调 + SessionSwitch + Dispatcher | **采纳 rev-gpt 加固版** + rev-glm 注释修正 |
| R6 | `ApplyTheme` swap 顺序 | 先删再加（可能闪烁） | 备选先加再删 | 索引替换（先备新再换旧） | **采纳 rev-gpt**：`dictionaries[index]=replacement` 索引替换 |
| R7 | 深色 SurfaceBrush 半透明 | #F21C2129（α=95%） | 🔴 改不透明（防壁纸穿透发灰） | 同意不透明 | **采纳**：深色 `#FF1C2129`、浅色 `#FFFFFFFF`；窗口整体 Opacity 承担透明 |
| R8 | AccentPressed 深色 #2563EB 上深字 3.5:1 | 🟡 瞬时可接受 | 上调到 #3B82F6 达 AA | 加 OnAccentPressedBrush | **采纳 rev-glm 简化**：深色 AccentPressedBrush = `#FF3B82F6`（与 Hover 同值，白字达 AA） |
| R9 | AppControls 里 AppFont 引用 | `{StaticResource AppFont}` | — | 可能解析失败 → DynamicResource | **采纳 rev-gpt**：`{DynamicResource AppFont}` |
| R10 | Wave 0 的 0b 是否删 App.xaml.cs SplitRatio | 删（替 Lane-M 扫雷） | 同意 | 反对（越权 Lane-M） | **终裁**：按**文件归属**切——0b 拥有 `App.xaml.cs`+`AppSettings.cs`，故 0b 删这两文件的 SplitRatio（107/418 + 62-63）；Lane-M 删自己拥有的 `MonthViewModel.cs:40`+`MonthWindow.xaml(.cs)`。每文件单 lane，零冲突 |
| R11 | M2 月格 TimeLabel | 🟡 验收期定 | 🔴 380px 不可行 | 限 38px | **用户裁决**：月格不显时间，仅底部详情显 |
| R12 | Messenger 注册/解绑 | 给代码但注释"ctor 追加" | 核对单例无泄漏 | 须 `IRecipient`+Dispose | **采纳 rev-gpt**：VM 实现 `IRecipient<CalendarColorsChangedMessage>` + ctor Register + Dispose Unregister；App 退出 Dispose |
| R13 | S4 部分提交 / IsBusy | 未提 | 加 IsBusy guard | 先存日历再存设置 + IsBusy | **采纳 rev-gpt**：先日历后设置 + IsBusy 防重入 |

**全一致项**（直接采纳 rev-kimi，不再列）：架构方向、token 集补 6 键、验证红并入 Danger、AccentSoft/TodayBackground 合并+TodayBorder 独立、W2 FontWeight 移入 Style Setter、W8 公式与 7/20→7月第4周、WeekOfMonth 放 Core、EqualsConverter、声明式色板 ControlTemplate.Triggers、M4 垂直居中、M5 发丝线、MonthSplitRatio 须清 4 文件。

---

## §3 最终权威代码（粘贴即用）

### §3.1 `Themes/Palette.Light.xaml`（含 R7 不透明 + R8）

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- surfaces（R7：不透明，透明感交由窗口 Opacity） -->
    <SolidColorBrush x:Key="SurfaceBrush"            Color="#FFFFFFFF" />
    <SolidColorBrush x:Key="HeaderBrush"             Color="#FFF1F3F9" />
    <SolidColorBrush x:Key="ControlBgBrush"          Color="#FFFFFFFF" />

    <!-- text -->
    <SolidColorBrush x:Key="ForegroundBrush"         Color="#FF1F2937" />
    <SolidColorBrush x:Key="MutedForegroundBrush"    Color="#FF646E7A" />
    <SolidColorBrush x:Key="OnAccentBrush"           Color="#FFFFFFFF" />

    <!-- borders -->
    <SolidColorBrush x:Key="BorderBrush"             Color="#FFE5E7EB" />
    <SolidColorBrush x:Key="BorderHoverBrush"        Color="#FFD1D5DB" />

    <!-- accent family（accent #2563EB 白字 5.1:1） -->
    <SolidColorBrush x:Key="AccentBrush"             Color="#FF2563EB" />
    <SolidColorBrush x:Key="AccentHoverBrush"        Color="#FF1D4ED8" />
    <SolidColorBrush x:Key="AccentPressedBrush"      Color="#FF1E40AF" />
    <SolidColorBrush x:Key="AccentSoftBrush"         Color="#FFDBEAFE" /> <!-- 当天淡填+all-day pill -->
    <SolidColorBrush x:Key="TodayBorderBrush"        Color="#FF93C5FD" />
    <SolidColorBrush x:Key="SelectionBrush"          Color="#402563EB" />

    <!-- danger（验证红+周末红+进行中红 统一） -->
    <SolidColorBrush x:Key="DangerBrush"             Color="#FFDC2626" />

    <!-- overlays -->
    <SolidColorBrush x:Key="HoverOverlayBrush"       Color="#14000000" />
    <SolidColorBrush x:Key="PressedOverlayBrush"     Color="#22000000" />
    <SolidColorBrush x:Key="DayCardBrush"            Color="#0D000000" />
    <SolidColorBrush x:Key="WeekendOverlayBrush"     Color="#14000000" />

    <!-- scrollbar -->
    <SolidColorBrush x:Key="ScrollBarThumbBrush"      Color="#59646E7A" />
    <SolidColorBrush x:Key="ScrollBarThumbHoverBrush" Color="#8C646E7A" />
    <SolidColorBrush x:Key="ScrollBarTrackBrush"      Color="Transparent" />

    <SolidColorBrush x:Key="SwatchRingBrush"         Color="#FFFFFFFF" />
</ResourceDictionary>
```

### §3.2 `Themes/Palette.Dark.xaml`（含 R7 不透明 + R8 AccentPressed=#3B82F6）

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- surfaces（R7：不透明 #FF，防壁纸穿透） -->
    <SolidColorBrush x:Key="SurfaceBrush"            Color="#FF1C2129" />
    <SolidColorBrush x:Key="HeaderBrush"             Color="#FF262D3A" />
    <SolidColorBrush x:Key="ControlBgBrush"          Color="#FF232A37" />

    <!-- text（对 #1C2129：FG 13.6:1 / Muted 6.9:1） -->
    <SolidColorBrush x:Key="ForegroundBrush"         Color="#FFE5E7EB" />
    <SolidColorBrush x:Key="MutedForegroundBrush"    Color="#FF9AA3B2" />
    <SolidColorBrush x:Key="OnAccentBrush"           Color="#FF0B1424" />

    <SolidColorBrush x:Key="BorderBrush"             Color="#FF39414F" />
    <SolidColorBrush x:Key="BorderHoverBrush"        Color="#FF4A5364" />

    <!-- accent（#60A5FA 对深卡 6.9:1） -->
    <SolidColorBrush x:Key="AccentBrush"             Color="#FF60A5FA" />
    <SolidColorBrush x:Key="AccentHoverBrush"        Color="#FF3B82F6" />
    <SolidColorBrush x:Key="AccentPressedBrush"      Color="#FF3B82F6" /> <!-- R8：与 Hover 同值，白字达 AA -->
    <SolidColorBrush x:Key="AccentSoftBrush"         Color="#FF2A3A55" />
    <SolidColorBrush x:Key="TodayBorderBrush"        Color="#FF3B82F6" />
    <SolidColorBrush x:Key="SelectionBrush"          Color="#4060A5FA" />

    <SolidColorBrush x:Key="DangerBrush"             Color="#FFF87171" /> <!-- 6.4:1 -->

    <SolidColorBrush x:Key="HoverOverlayBrush"       Color="#1FFFFFFF" />
    <SolidColorBrush x:Key="PressedOverlayBrush"     Color="#33FFFFFF" />
    <SolidColorBrush x:Key="DayCardBrush"            Color="#12FFFFFF" />
    <SolidColorBrush x:Key="WeekendOverlayBrush"     Color="#1FFFFFFF" />

    <SolidColorBrush x:Key="ScrollBarThumbBrush"      Color="#599AA3B2" />
    <SolidColorBrush x:Key="ScrollBarThumbHoverBrush" Color="#8C9AA3B2" />
    <SolidColorBrush x:Key="ScrollBarTrackBrush"      Color="Transparent" />

    <SolidColorBrush x:Key="SwatchRingBrush"         Color="#FFFFFFFF" />
</ResourceDictionary>
```

### §3.3 Token 键清单（24 个，Wave 0.0 冻结契约）

`SurfaceBrush` `HeaderBrush` `ControlBgBrush` `ForegroundBrush` `MutedForegroundBrush` `OnAccentBrush` `BorderBrush` `BorderHoverBrush` `AccentBrush` `AccentHoverBrush` `AccentPressedBrush` `AccentSoftBrush` `TodayBorderBrush` `SelectionBrush` `DangerBrush` `HoverOverlayBrush` `PressedOverlayBrush` `DayCardBrush` `WeekendOverlayBrush` `ScrollBarThumbBrush` `ScrollBarThumbHoverBrush` `ScrollBarTrackBrush` `SwatchRingBrush` + `AppFont`（FontFamily，App.xaml 根）。

### §3.4 `App.xaml`（R6 顺序：Palette 最先 merge）

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary Source="Themes/Palette.Light.xaml" />   <!-- 必须首位，ApplyTheme 按名替换 -->
            <ResourceDictionary Source="Themes/FontSizes.S.xaml" />
            <ResourceDictionary Source="Themes/AppControls.xaml" />
        </ResourceDictionary.MergedDictionaries>
        <FontFamily x:Key="AppFont">Microsoft YaHei UI</FontFamily>
        <!-- HeaderBrushWeek/HeaderBrushMonth 已删除；tb:TaskbarIcon 块保留不变 -->
        ...
    </ResourceDictionary>
</Application.Resources>
```

### §3.5 `Services/SystemThemeWatcher.cs`（R5 加固版：弱引用 + SessionSwitch + Dispatcher）

```csharp
using System.Windows;
using System.Windows.Threading;
using CalDesk.App.Models;
using Microsoft.Win32;

namespace CalDesk.App.Services;

/// <summary>Watches OS per-app theme (HKCU\...\Personalize\AppsUseLightTheme).
/// R5: SystemEvents fires on its OWN worker thread (NOT UI) — must hop to UI thread.
/// Uses weak static target + SessionSwitch (unlock/RDP) + explicit Dispose unsubscribe.</summary>
public sealed class SystemThemeWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private bool _started;
    private static WeakReference<SystemThemeWatcher>? _current;

    public SystemThemeWatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public event Action<ThemeMode>? EffectiveThemeChanged;

    public void Start()
    {
        if (_started) return;
        _current = new WeakReference<SystemThemeWatcher>(this);
        SystemEvents.UserPreferenceChanged += StaticPreferenceChanged;
        SystemEvents.SessionSwitch += StaticSessionSwitch;
        _started = true;
    }

    public void Dispose()
    {
        if (!_started) return;
        SystemEvents.UserPreferenceChanged -= StaticPreferenceChanged;
        SystemEvents.SessionSwitch -= StaticSessionSwitch;
        _started = false;
    }

    private static void StaticPreferenceChanged(object? s, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color) Notify();
    }
    private static void StaticSessionSwitch(object? s, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect) Notify();
    }
    private static void Notify()
    {
        if (_current?.TryGetTarget(out var w) != true) return;
        w._dispatcher.BeginInvoke(DispatcherPriority.DataBind,
            new Action(() => w.EffectiveThemeChanged?.Invoke(SystemThemeWatcher.GetEffectiveTheme())));
    }

    public static ThemeMode GetEffectiveTheme()
    {
        try
        {
            var v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return v is int i && i == 0 ? ThemeMode.Dark : ThemeMode.Light;
        }
        catch { return ThemeMode.Light; }
    }
}
```

### §3.6 `App.xaml.cs` 片段（R6 索引替换 + 退出 Dispose + 字号切换通知 MonthWindow）

```csharp
private SystemThemeWatcher? _themeWatcher;

// OnStartup，紧随 ApplyFontSize(...) 之后：
ApplyTheme(_settingsSvc.Current.Theme);
_themeWatcher = new SystemThemeWatcher(Dispatcher);
_themeWatcher.EffectiveThemeChanged += t =>
{
    if (_settingsSvc.Current.Theme == ThemeMode.FollowSystem) ApplyTheme(ThemeMode.FollowSystem);
};
_themeWatcher.Start();

// OnExit（_reminders?.Dispose() 之前）：
_themeWatcher?.Dispose();

/// <summary>R6: 索引替换（先备新字典再换旧），避免中间 null 闪烁。</summary>
public void ApplyTheme(ThemeMode mode)
{
    var effective = mode == ThemeMode.FollowSystem
        ? SystemThemeWatcher.GetEffectiveTheme() : mode;
    var src = effective == ThemeMode.Dark ? "Themes/Palette.Dark.xaml" : "Themes/Palette.Light.xaml";
    var replacement = new ResourceDictionary { Source = new Uri(src, UriKind.Relative) };

    var dict = Resources.MergedDictionaries;
    int idx = -1;
    for (var i = 0; i < dict.Count; i++)
        if (dict[i].Source?.OriginalString.Contains("/Palette.", StringComparison.Ordinal) == true
            || dict[i].Source?.OriginalString.EndsWith("Palette.Light.xaml", StringComparison.Ordinal) == true
            || dict[i].Source?.OriginalString.EndsWith("Palette.Dark.xaml", StringComparison.Ordinal) == true)
        { idx = i; break; }
    if (idx >= 0) dict[idx] = replacement; else dict.Insert(0, replacement);
}

// ApplyFontSize 末尾追加（R4：字号切换重算月视图限值）：
foreach (var w in Application.Current.Windows.OfType<Views.MonthWindow>()) w.ComputeBottomLimits();
```

**0b 顺带删除 App.xaml.cs 的 SplitRatio 两行**（`:107 _monthVm.SplitRatio=...` 与 `:418 s.MonthSplitRatio=_monthVm.SplitRatio;`）——这些文件归 0b。

### §3.7 `Models/AppSettings.cs`（ThemeMode + 删 MonthSplitRatio）

```csharp
public enum ThemeMode { FollowSystem, Light, Dark }
// 类内：
public ThemeMode Theme { get; set; } = ThemeMode.Light;
// 删除 MonthSplitRatio 属性（原 :62-63）。旧 settings.json 残留字段由 System.Text.Json 忽略，无害。
```

### §3.8 `AppControls.xaml` 关键块（R3 keyed SlimScrollBar + 全 Track 绑定 + SlimScrollViewer）

> Card* 系列样式：按 §3.13 hex→token 映射表把每个 hex 替换为对应 `{DynamicResource Token}`，FontFamily 替换为 `{DynamicResource AppFont}`（R9）。结构不变。GhostButton/RowButton/ToggleSegment/ToggleSegmentActive 从 Week/Month 吸收为同键（rev-kimi §C 内容直接用）。

```xml
<!-- R3：keyed（非隐式），SlimScrollViewer 模板内显式引用，native 弹出层零影响 -->
<Style x:Key="SlimScrollBar" TargetType="ScrollBar">
    <Setter Property="Background" Value="{DynamicResource ScrollBarTrackBrush}" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="ScrollBar">
                <Grid Background="{TemplateBinding Background}" SnapsToDevicePixels="True">
                    <Track x:Name="PART_Track" Orientation="{TemplateBinding Orientation}"
                           Minimum="{TemplateBinding Minimum}"
                           Maximum="{TemplateBinding Maximum}"
                           Value="{TemplateBinding Value}"
                           ViewportSize="{TemplateBinding ViewportSize}">
                        <Track.Thumb>
                            <Thumb Margin="1">
                                <Thumb.Template>
                                    <ControlTemplate TargetType="Thumb">
                                        <Border x:Name="Bd" CornerRadius="3" Width="6"
                                                Background="{DynamicResource ScrollBarThumbBrush}" />
                                        <ControlTemplate.Triggers>
                                            <Trigger Property="IsMouseOver" Value="True">
                                                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource ScrollBarThumbHoverBrush}" />
                                            </Trigger>
                                            <Trigger Property="IsDragging" Value="True">
                                                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource ScrollBarThumbHoverBrush}" />
                                            </Trigger>
                                        </ControlTemplate.Triggers>
                                    </ControlTemplate>
                                </Thumb.Template>
                            </Thumb>
                        </Track.Thumb>
                    </Track>
                </Grid>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>

<!-- overlay 滚动条不占列；调用方给右 Padding 预留（见 §3.9） -->
<Style x:Key="SlimScrollViewer" TargetType="ScrollViewer">
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="ScrollViewer">
                <Grid Background="{TemplateBinding Background}">
                    <ScrollContentPresenter Margin="{TemplateBinding Padding}"
                                            Content="{TemplateBinding Content}"
                                            ContentTemplate="{TemplateBinding ContentTemplate}"
                                            CanContentScroll="{TemplateBinding CanContentScroll}" />
                    <ScrollBar x:Name="PART_VerticalScrollBar" Style="{StaticResource SlimScrollBar}"
                               Orientation="Vertical" HorizontalAlignment="Right"
                               Maximum="{TemplateBinding ScrollableHeight}"
                               ViewportSize="{TemplateBinding ViewportHeight}"
                               Value="{Binding VerticalOffset, RelativeSource={RelativeSource TemplatedParent}, Mode=OneWay}"
                               Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}" />
                    <ScrollBar x:Name="PART_HorizontalScrollBar" Style="{StaticResource SlimScrollBar}"
                               Orientation="Horizontal" VerticalAlignment="Bottom"
                               Maximum="{TemplateBinding ScrollableWidth}"
                               ViewportSize="{TemplateBinding ViewportWidth}"
                               Value="{Binding HorizontalOffset, RelativeSource={RelativeSource TemplatedParent}, Mode=OneWay}"
                               Visibility="{TemplateBinding ComputedHorizontalScrollBarVisibility}" />
                </Grid>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

**调用方**（Week:118 / Month:241）：`Style="{StaticResource SlimScrollViewer}"` + `Padding="4,0,12,0"`（右 12 预留 overlay 不遮内容；左 4 对称——W1 修复）。

### §3.9 周视图 W1/W2/W4/W5/W6（ItemTemplate 关键 XAML）

```xml
<!-- W1：ScrollViewer 对称 Padding + SlimScrollViewer -->
<ScrollViewer Grid.Row="1" Style="{StaticResource SlimScrollViewer}"
              VerticalScrollBarVisibility="Auto" Padding="4,0,12,0">
  <ItemsControl ItemsSource="{Binding Week.Days}">
    <ItemsControl.ItemTemplate>
      <DataTemplate>
        <!-- W2/W4：日卡 Padding 12,8；bg/border 走 Style.Triggers（非本地属性） -->
        <Border CornerRadius="6" Padding="12,8" Margin="0,4" BorderThickness="1">
          <Border.Style>
            <Style TargetType="Border">
              <Setter Property="Background" Value="{DynamicResource DayCardBrush}"/>
              <Setter Property="BorderBrush" Value="Transparent"/>
              <Style.Triggers>
                <DataTrigger Binding="{Binding IsWeekend}" Value="True">
                  <Setter Property="Background" Value="{DynamicResource WeekendOverlayBrush}"/>
                </DataTrigger>
                <DataTrigger Binding="{Binding IsToday}" Value="True">
                  <Setter Property="Background" Value="{DynamicResource AccentSoftBrush}"/>
                  <Setter Property="BorderBrush" Value="{DynamicResource TodayBorderBrush}"/>
                </DataTrigger>
              </Style.Triggers>
            </Style>
          </Border.Style>
          <StackPanel>
            <!-- 日期 Header：W2 SemiBold 必须在 Style Setter，IsToday→Bold 才生效 -->
            <TextBlock Text="{Binding Header}" FontSize="{DynamicResource FontBody}">
              <TextBlock.Style>
                <Style TargetType="TextBlock">
                  <Setter Property="FontWeight" Value="SemiBold"/>
                  <Style.Triggers>
                    <DataTrigger Binding="{Binding IsToday}" Value="True">
                      <Setter Property="FontWeight" Value="Bold"/>
                      <Setter Property="Foreground" Value="{DynamicResource AccentBrush}"/>
                    </DataTrigger>
                  </Style.Triggers>
                </Style>
              </TextBlock.Style>
            </TextBlock>
            <!-- W5/W6：timed 行 Grid 3 列，点居中，Summary 省略号 -->
            <Button Style="{StaticResource RowButton}" Margin="0,1"
                    Command="{Binding DataContext.EditCommand, RelativeSource={RelativeSource AncestorType=Window}}"
                    CommandParameter="{Binding EventId}">
              <Grid>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width="46"/>  <!-- TimeLabel -->
                  <ColumnDefinition Width="Auto"/> <!-- 色点 -->
                  <ColumnDefinition Width="*"/>    <!-- Summary -->
                </Grid.ColumnDefinitions>
                <TextBlock Grid.Column="0" Text="{Binding TimeLabel}"
                           FontSize="{DynamicResource FontSmall}"
                           Foreground="{DynamicResource AccentBrush}" VerticalAlignment="Center"/>
                <Border Grid.Column="1" Width="5" Height="5" CornerRadius="2" Margin="0,0,5,0"
                        VerticalAlignment="Center" Opacity="0.9"
                        Background="{Binding Color, Converter={StaticResource ColorOrAccent}}"/>
                <TextBlock Grid.Column="2" Text="{Binding Summary}" VerticalAlignment="Center"
                           FontSize="{DynamicResource FontBody}"
                           TextTrimming="CharacterEllipsis" TextWrapping="NoWrap">
                  <TextBlock.Style>
                    <Style TargetType="TextBlock">
                      <Style.Triggers>
                        <DataTrigger Binding="{Binding IsOngoing}" Value="True">
                          <Setter Property="FontWeight" Value="Bold"/>
                          <Setter Property="Foreground" Value="{DynamicResource DangerBrush}"/>
                        </DataTrigger>
                      </Style.Triggers>
                    </Style>
                  </TextBlock.Style>
                </TextBlock>
              </Grid>
            </Button>
            <!-- all-day 行：同构 Grid 2 列，外裹 AccentSoftBrush pill Border（略，见 rev-kimi §E） -->
          </StackPanel>
        </Border>
      </DataTemplate>
    </ItemsControl.ItemTemplate>
  </ItemsControl>
</ScrollViewer>
```

> `ColorOrAccent` 见 §3.11。all-day 行同模式（dot + Summary，外裹 `<Border Background="{DynamicResource AccentSoftBrush}" CornerRadius="4" Padding="6,2">`）。

### §3.10 `Core/Services/WeekOfMonthCalculator.cs`（W8，放 Core 可 Linux 测）

```csharp
namespace CalDesk.Core.Services;

/// <summary>W8：M月第W周。ISO-8601 周四锚——跨月周归属多数天所在月。
/// 调用方须保证传入周一（WeekStart=Monday）。7/20→7月第4周。</summary>
public static class WeekOfMonthCalculator
{
    public static (int Month, int Week) Calculate(DateOnly monday)
    {
        System.Diagnostics.Debug.Assert(monday.DayOfWeek == DayOfWeek.Monday,
            "WeekOfMonthCalculator expects a Monday; week-start change requires re-deriving the Thursday anchor.");
        var thu = monday.AddDays(3);
        return (thu.Month, ((thu.Day - 1) / 7) + 1);
    }
    public static string Header(DateOnly monday)
    {
        var (m, w) = Calculate(monday);
        return $"{m}月第{w}周";
    }
}
```

`WeekViewModel.NavigateTo`：`WeekHeader = WeekOfMonthCalculator.Header(DateOnly.FromDateTime(monday));`

**单测矩阵**（`tests/CalDesk.Tests/WeekOfMonthCalculatorTests.cs`）：
`2026-06-29→7月第1周` `2026-07-06→7月第2周` `2026-07-13→7月第3周` `2026-07-20→7月第4周` `2026-07-27→7月第5周` `2025-12-29→1月第1周` `2026-08-31→9月第1周` `2026-06-01→6月第1周`。

### §3.11 `Converters/ColorOrAccentConverter.cs` + `BrushResourceProxy.cs`（R2 结构性正解）

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CalDesk.App.Converters;

/// <summary>R2：用 Freezable 代理承载 {DynamicResource AccentBrush}，主题切换时 DP 通知 → MultiBinding 重算。
/// 取代 TargetNullValue（非 DP，不能挂 DynamicResource）。</summary>
public sealed class BrushResourceProxy : Freezable
{
    public Brush? Brush { get => (Brush?)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public static readonly DependencyProperty BrushProperty =
        DependencyProperty.Register(nameof(Brush), typeof(Brush), typeof(BrushResourceProxy));
    protected override Freezable CreateInstanceCore() => new BrushResourceProxy();
}

public sealed class ColorOrAccentConverter : IMultiValueConverter
{
    private static readonly BrushConverter C = new();
    public object Convert(object[] v, Type t, object p, CultureInfo c)
    {
        if (v.Length > 0 && v[0] is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try { return C.ConvertFromString(hex) as Brush ?? Fallback(v); }
            catch (FormatException) { /* fall through */ }
        }
        return Fallback(v);
    }
    private static object Fallback(object[] v) => v.Length > 1 && v[1] is Brush b ? b : Brushes.Transparent;
    public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}
```

**App/Window 资源**：
```xml
<conv:ColorOrAccentConverter x:Key="ColorOrAccent"/>
<conv:BrushResourceProxy x:Key="AccentProxy" Brush="{DynamicResource AccentBrush}"/>
```
**绑定（5 处色点统一）**：
```xml
<MultiBinding Converter="{StaticResource ColorOrAccent}">
    <Binding Path="Color"/>
    <Binding Source="{StaticResource AccentProxy}" Path="Brush"/>
</MultiBinding>
```

### §3.12 设置 S3 声明式色板（ControlTemplate.Triggers；OnPickColor 单选不清除）

```xml
<!-- Window.Resources -->
<conv:EqualsConverter x:Key="EqualsConv"/>
<!-- S3：色板 ItemsControl（DataTemplate）-->
<Button Width="20" Height="20" Margin="2,0" Padding="0" Cursor="Hand" ToolTip="{Binding}" Click="OnPickColor">
  <Button.Template>
    <ControlTemplate TargetType="Button">
      <Border x:Name="Ring" CornerRadius="5" BorderThickness="2" BorderBrush="Transparent" Background="Transparent">
        <Grid>
          <Border CornerRadius="3" Background="{Binding}"/>
          <Border x:Name="InnerRing" CornerRadius="3" BorderBrush="{DynamicResource SwatchRingBrush}" BorderThickness="1.5" Visibility="Collapsed"/>
          <Path x:Name="Check" Data="M 4,8.5 L 7,11.5 L 12,5" Stroke="{DynamicResource SwatchRingBrush}"
                StrokeThickness="1.8" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed"/>
        </Grid>
      </Border>
      <ControlTemplate.Triggers>
        <DataTrigger Value="True">
          <DataTrigger.Binding>
            <MultiBinding Converter="{StaticResource EqualsConv}">
              <Binding/>
              <Binding Path="DataContext.Color" RelativeSource="{RelativeSource AncestorType=ItemsControl}"/>
            </MultiBinding>
          </DataTrigger.Binding>
          <Setter TargetName="Ring" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/>
          <Setter TargetName="InnerRing" Property="Visibility" Value="Visible"/>
          <Setter TargetName="Check" Property="Visibility" Value="Visible"/>
        </DataTrigger>
        <Trigger Property="IsMouseOver" Value="True">
          <Setter TargetName="Ring" Property="Background" Value="{DynamicResource HoverOverlayBrush}"/>
        </Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
  </Button.Template>
</Button>
```

`EqualsConverter`（`IMultiValueConverter`，OrdinalIgnoreCase 比 hex）。`OnPickColor`：`row.Color = hex;`（单选，不 toggle-off）。`SettingsWindow.xaml.cs` 删除 `_swatchHandlers/OnSwatchPanelLoaded/PaintSwatchRings/FindAncestorItemsControl/FindVisualChildren/DetachSwatchHandlers/OnUnloaded` 及 XAML 的 `Loaded="OnSwatchPanelLoaded"`；保留 `OnPasswordChanged/OnClose/FindAncestorDataContext<T>`。

### §3.13 hex→token 映射表（机械迁移，Lane-W/M/Q/S 通用）

| 原 hex | token | 原 hex | token |
|---|---|---|---|
| `#3B82F6`/`#FF3B82F6` | AccentBrush | `#1F2937`/`#FF1F2937` | ForegroundBrush |
| `#2563EB`/`#FF2563EB` | AccentHoverBrush | `#6B7280`/`#FF6B7280` | MutedForegroundBrush |
| `#1D4ED8`/`#1E40AF` | AccentHover/Pressed | `#FFFFFFFF`（文字/勾） | OnAccentBrush 或 SwatchRingBrush |
| `#223B82F6`/`#143B82F6`（当天/pill 淡填）| AccentSoftBrush | `#E5E7EB`/`#FFE5E7EB` | BorderBrush |
| `#93C5FD`（当天边框，新增） | TodayBorderBrush | `#D1D5DB`/`#FFD1D5DB` | BorderHoverBrush |
| `#B91C1C`/`#DC2626` | DangerBrush | `#F4FFFFFF`/`#F7FFFFFF`（卡面） | SurfaceBrush |
| `#0D000000`（日卡底） | DayCardBrush | `#F1F3F9`/`#F6F1F4`（header） | HeaderBrush |
| `#14000000`（hover/toggle/weekend） | HoverOverlayBrush 或 WeekendOverlayBrush（按语义） | `#22000000`（pressed） | PressedOverlayBrush |
| `Microsoft YaHei UI` | `{DynamicResource AppFont}` | `#223B82F6`（TextBox 选区） | SelectionBrush |

> Lane-S/M/W 迁移后执行 `rg '#[0-9A-Fa-f]{6,8}' src/CalDesk.App --glob '*.xaml'` 应仅剩注释。

### §3.14 S4 `SavePropertiesAsync`（R13 先日历后设置 + IsBusy）+ Messenger（R12）

```csharp
[RelayCommand]
private async Task SavePropertiesAsync()
{
    if (IsBusy) return;            // R13 防重入
    IsBusy = true;
    try
    {
        await SaveCalendarRowsAsync();          // 先日历（失败则不写设置，避免部分提交）

        _settings.WindowMode = WindowMode;
        _settings.Opacity = Math.Clamp(Opacity, 0.15, 1.0);
        _settings.LockedPosition = WindowMode == WindowMode.Desktop && LockedPosition;
        _settings.StartWithWindows = StartWithWindows;
        _settings.DefaultDurationMinutes = DefaultDurationMinutes;
        _settings.DefaultReminderMinutes = DefaultReminderMinutes;
        _settings.DefaultCalendarId = SelectedCalendar?.Id;
        _settingsService.Save(_settings);

        StartupHelper.Set(StartWithWindows, Environment.ProcessPath ?? AppExePath());

        WeakReferenceMessenger.Default.Send(new CalendarColorsChangedMessage()); // 刷新已开窗口，无服务器同步
        RequestClose?.Invoke();
    }
    finally { IsBusy = false; }
}

private async Task SaveCalendarRowsAsync()
{
    var stored = (await _store.Calendars.GetAllAsync()).ToDictionary(c => c.Id);
    foreach (var row in CalendarRows)
    {
        if (!stored.TryGetValue(row.Id, out var cur)) continue;
        if (cur.IsSelected != row.IsSelected || !StringEquals(cur.Color, row.Color))
            await _store.Calendars.UpsertAsync(row.ToCalendarInfo(cur));
    }
}
// 删除 [RelayCommand] SaveCalendarsAsync（原 :307-351）；删除 XAML 保存日历按钮（:153-154）。
// using CommunityToolkit.Mvvm.Messaging;
```

```csharp
// Models/CalendarColorsChangedMessage.cs
namespace CalDesk.App.Models;
public sealed record CalendarColorsChangedMessage;

// WeekViewModel / MonthViewModel（R12）：
public sealed partial class WeekViewModel : ObservableObject, IRecipient<CalendarColorsChangedMessage>, IDisposable
{
    // ctor 末尾：WeakReferenceMessenger.Default.Register(this);
    public void Receive(CalendarColorsChangedMessage _) { Interlocked.Increment(ref _navVersion); _ = LoadAsync(); }
    public void Dispose() => WeakReferenceMessenger.Default.Unregister<CalendarColorsChangedMessage>(this);
}
// MonthViewModel 同构。App.OnExit：_vm?.Dispose(); _monthVm?.Dispose();
```

### §3.15 月视图 M2/M3/M4/M5

**M2（月格）**：删 `MaxWidth="34"`；行 = `[Auto 色点][* Summary]`（**无 TimeLabel**，R11）；Summary `TextTrimming=CharacterEllipsis`。
**M2（底部详情）**：timed/all-day 行用 §3.9 的 W5/W6 Grid 3 列模板（含 TimeLabel）。
**M3**：见 §3.16。
**M4**：`MonthWindow.xaml:174` `<StackPanel VerticalAlignment="Center">`。
**M5**：`Grid.Row="3"` GridSplitter → `<Border BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,0,1" Margin="0,6,0,4" SnapsToDevicePixels="True"/>`。

### §3.16 M3 `MonthWindow.ComputeBottomLimits()`（R4 终裁：code-behind 为主）

```csharp
// MonthWindow.xaml.cs
public void ComputeBottomLimits()
{
    double body = (double)Application.Current.FindResource("FontBody");      // 当前字号档
    double tiny = (double)Application.Current.FindResource("FontTiny");
    double micro = (double)Application.Current.FindResource("FontMicro");
    double small = (double)Application.Current.FindResource("FontSmall");
    double headerFs = (double)Application.Current.FindResource("FontHeader");

    double cellRow = Math.Ceiling(tiny * 1.35) + 2;        // 日格事件行
    double datePill = Math.Ceiling(small * 1.3) + 4;       // 日格日期药丸
    double cellMin = datePill + 3 * cellRow + 2;           // 每格 3 事件
    double gridMin = 6 * cellMin;                          // R4：Row2.MinHeight = 6×cell（非 180）

    double detailRow = Math.Ceiling(body * 1.35) + 6;
    double detailHdr = Math.Ceiling(body * 1.3) + 10;
    double detailMin = detailHdr + 3 * detailRow;          // 底部 3 行
    double detailMax = detailHdr + 10 * detailRow;         // 底部 10 行封顶

    RootGrid.RowDefinitions[2].MinHeight = gridMin;
    RootGrid.RowDefinitions[4].MinHeight = detailMin;
    RootGrid.RowDefinitions[4].MaxHeight = detailMax;

    double chrome = 24 + (Math.Ceiling(headerFs*1.3)+20) + (Math.Ceiling(micro*1.3)+8) + 10 + (Math.Ceiling(small*1.3)+18);
    MinHeight = chrome + gridMin + detailMin;              // ≈600 @ S（spec 要求）
}
// 挂载：OnLoaded 调用；App.ApplyFontSize 末尾循环调用（§3.6）。
// Window 默认 Height 520 → 上调到 ~640（开窗即高于 MinHeight）。
```

**MonthWindow.xaml RowDefinitions**：
```xml
<RowDefinition Height="Auto"/>                              <!-- header -->
<RowDefinition Height="Auto"/>                              <!-- weekday -->
<RowDefinition Height="3*"/>                                <!-- 月历（MinHeight 由 code-behind 设） -->
<RowDefinition Height="Auto"/>                              <!-- 发丝线（M5） -->
<RowDefinition Height="2*"/>                                <!-- 详情（Min/MaxHeight 由 code-behind 设） -->
<RowDefinition Height="Auto"/>                              <!-- status -->
<!-- 删 GridSplitter 元素；删 Loaded="OnLoaded"→改为保留 OnLoaded 仅调 ComputeBottomLimits（删 OnSplitChanged/ApplySplitRatio） -->
```

**MonthSplitRatio 清理（按文件归属）**：
- 0b：`App.xaml.cs:107/418` + `AppSettings.cs:62-63`。
- Lane-M：`MonthViewModel.cs:40`（删 SplitRatio）+ `MonthWindow.xaml.cs:85-110`（删三方法）+ `OnDrag` 的 `FindAncestor<GridSplitter>` 分支 + `using System.Windows.Controls.Primitives;` + `MonthWindow.xaml` 的 `GridSplitter` 与 `Loaded="OnLoaded"`（若 OnLoaded 仅用于 split）。

### §3.17 主题三选 UI（`SettingsWindow.xaml` 显示 GroupBox）

字号单选组旁加主题单选组：`跟随系统 / 浅色 / 深`，`IsChecked="{Binding Theme, Converter={StaticResource EnumToBool}, ConverterParameter={x:Static m:ThemeMode.FollowSystem}, Mode=TwoWay}"`（余两类推）。`SettingsViewModel`：`[ObservableProperty] private ThemeMode _theme = ThemeMode.Light;` + `partial void OnThemeChanged(ThemeMode v){ (Application.Current as App)?.ApplyTheme(v); _settings.Theme=v; _settingsService.Save(_settings); }` + `InitializeAsync` 设 `Theme=_settings.Theme;`。

---

## §4 Wave / Task / Lane 终稿

### T0.0 Token 契约冻结（先决，零代码）
冻结 §3.3 的 24 键名 + `ThemeMode` 枚举名 + `CalendarColorsChangedMessage` 类型名。冻结后各 lane 凭名并行。

### Wave 0 — 地基（3 lane 并行，文件级零交叠）

| Lane | 文件所有权 | 任务 |
|---|---|---|
| **0a** | `Themes/Palette.Light.xaml`、`Themes/Palette.Dark.xaml`、`Themes/FontSizes.{S,M,L}.xaml`、`App.xaml` | §3.1/§3.2/§3.4；FontSizes 三档**不改**（M3 用 code-behind，不再加键） |
| **0b** | `Models/AppSettings.cs`、`Models/CalendarColorsChangedMessage.cs`、`Services/SystemThemeWatcher.cs`、`App.xaml.cs`、`Converters/{EqualsConverter,ColorOrAccentConverter,BrushResourceProxy}.cs` | §3.5/§3.6/§3.7/§3.11/§3.14(message)；**顺带删 App.xaml.cs:107/418 + AppSettings.cs:62-63 的 SplitRatio**（R10，归 0b） |
| **0c** | `Themes/AppControls.xaml` | 全文重构：Card* 系列 hex→token（§3.13）+ FontFamily→`{DynamicResource AppFont}`(R9) + 吸收 GhostButton/RowButton/ToggleSegment/ToggleSegmentActive + §3.8 SlimScrollBar(keyed)+SlimScrollViewer |

**Wave 0 出口门**：`ssh winbuild 'cd C:\Dev_Code\caldesk && git pull && dotnet build CalDesk.slnx'` 绿；浅色视觉与改造前逐项一致；手动切深色两窗口即时重绘；`rg HeaderBrushWeek` 零命中。
**并行性**：0a/0b/0c 文件不交叠；0c 引用 0a 键名但键名已 T0.0 冻结，可并行开发，仅合入需 0a 先到。

### Wave 1 — 业务（4 lane 并行，入口门 = Wave 0 出口门）

| Lane | 文件所有权 | 任务 |
|---|---|---|
| **S** | `Views/SettingsWindow.xaml(.cs)`、`ViewModels/SettingsViewModel.cs` | S1(`:45`→"地址")、S2(`:90`→"保存连接信息")、S3(§3.12 + 精简 xaml.cs)、S4(§3.14)、主题三选(§3.17)、SettingsViewModel.Theme、删`保存日历`(`:153-154`)、Settings 内 hex 迁移 |
| **W** | `Views/WeekWindow.xaml(.cs)`、`ViewModels/WeekViewModel.cs`、`Core/Services/WeekOfMonthCalculator.cs`(新)、`tests/CalDesk.Tests/WeekOfMonthCalculatorTests.cs`(新) | W1/W2/W4/W5/W6(§3.9)、W8(§3.10+单测)、WeekViewModel 注册 Messenger+Dispose(R12)、删本地 4 样式、hex→token(含 :144/179 `#3B82F6`)、TargetNullValue `:161/:182`→§3.11 MultiBinding、AppFont、SlimScrollViewer |
| **M** | `Views/MonthWindow.xaml(.cs)`、`ViewModels/MonthViewModel.cs` | M1(header unify)、M2(§3.15 月格无时间+底部详情带时间)、M3(§3.16 ComputeBottomLimits)、M4、M5、MonthSplitRatio 清理(本 lane 文件)、MonthViewModel 注册 Messenger+Dispose、hex→token(含 :181/321)、TargetNullValue `:212/:302/:324`→§3.11（**含 R1 漏算的 :212**） |
| **Q** | `Views/QuickAddWindow.xaml`、`Views/AboutWindow.xaml` | QuickAdd 11 hex→token（`#DC2626`→Danger 等，§3.13）；About 0 处 |

**Wave 1 出口门**：winbuild build 绿；`rg '#[0-9A-Fa-f]{6,8}' src/CalDesk.App --glob '*.xaml'` 仅剩注释；`rg 'SaveCalendars'`、`rg 'MonthSplitRatio'`、`rg 'HeaderBrushWeek'` 全仓零命中。

### Wave 2 — 验证（§5）

---

## §5 验证门

**Linux**（`DOTNET_ROOT=$HOME/.dotnet`）：
- `dotnet build CalDesk.slnx -c Debug`（Core/CalDav/Storage/Tests 绿；App 不编）
- `dotnet test tests/CalDesk.Tests --nologo`：WeekOfMonth 8 用例全绿；原有 45 用例不回归。
- 注：`EqualsConverter/ColorOrAccentConverter/BrushResourceProxy/SystemThemeWatcher` 在 App(WPF)，Linux 不可测 → 由 winbuild 视觉覆盖，**不造假测试**。

**Windows（`ssh winbuild`）**：build + 截图验收清单：
1. 浅/深/跟随系统三态切换即时生效；**无色日历色点随主题正确变 accent**（验 R2 BrushResourceProxy）。
2. 锁屏/解锁/RDP 重连后跟随系统模式刷新（验 R5 SessionSwitch）。
3. 当天日卡：AccentSoftBrush 淡填 + TodayBorderBrush 边框 + 日期 Bold（验 W2 三重信号）。
4. 色板选中三件套（外环+内白+勾）+ 再点已选不清除（验 S3）。
5. 主"保存"改色后周/月色点即时更新 + **状态栏无同步流量**（验 S4 无服务器同步）。
6. 月视图拉高→详情封顶 10 行；压缩→不低于 3 行；GridSplitter 消失；发丝线 1px（验 M3/M5）。
7. 月窗 MinWidth=380 下 SlimScrollViewer overlay 不遮内容；周窗 MinWidth 下亦然（验 R3/W1）。
8. S/M/L 字号切换→月视图限值重算（验 §3.6 ApplyFontSize 通知 + §3.16）。
9. 周标题 = "M月第W周"，今天 2026-07-20 显示"7月第4周"（验 W8）。
10. QuickAdd 验证红与删除红一致（验 §3.13 Danger 合并）。
11. 768p 屏幕下 SettingsWindow 加主题行后不溢出（验 SizeToContent）。
12. 深色壁纸（浅/深/花）下 Surface 不发灰（验 R7 不透明）。

---

## §6 残留风险与 winbuild 必确认

**🔴 派发前已知/接受**：
- 窗口最小高度显著抬升（月窗 MinHeight ≈ 600 @ S，默认 Height 520→~640）——**用户 spec 明示**要"每格 3 行+底部 3 行"，属预期。开窗高度上调即可。
- 月格不显事件时间（R11/§3.15）——用户已裁决，时间在底部详情显。

**🟡 winbuild 实测确认（不阻塞派发，阻塞验收）**：
1. `SystemEvents.UserPreferenceChanged` 在 Win11 23H2+ 切主题的 `Category`（代码 General/Color 都接）。
2. MinWidth=380 下 SlimScrollViewer overlay 是否遮日卡 ＋ 按钮（右 Padding 12 是否够）。
3. 行高公式系数（tiny×1.35 / body×1.35）在 YaHei UI 实测下是否需微调（ComputeBottomLimits 改系数即可，无需改架构）。
4. 深色 DropShadowEffect 在深色壁纸上几乎不可见——可选：深色加 `Border` 或提 `Opacity=0.4`（非阻塞）。
5. `ApplyTheme` 字典替换在透明窗下是否闪烁（R6 索引替换已缓解；若仍闪，回退"先 Insert 再 Remove 旧"）。

**🟡 美学验收点**：
- 浅色 swatch（粉/琥珀/黄绿）上白色勾+内描边对比——决策 8 锁定，若验收不接受，备选勾加 0.5px 深投影（需回用户）。

**架构确定性**：R3（ScrollBar 全绑定）、R2（BrushResourceProxy）、R4（ComputeBottomLimits）、R5（弱引用+SessionSwitch）、R6（索引替换）、R10（文件归属切 SplitRatio）均为确定性实现，无机制赌博。唯一原机制赌博（RowDefinition 上 DynamicResource）已由 R4 绕开。

---

## 附录：派发指令模板（供 orchestrator 执行期使用）

```
Wave 0（3 并行 background fixer）:
  - fixer @ 0a: §3.1/§3.2/§3.4
  - fixer @ 0b: §3.5/§3.6/§3.7/§3.11/§3.14(message) + 删 SplitRatio(App.xaml.cs:107/418, AppSettings.cs:62-63)
  - fixer @ 0c: §3.8 + Card* hex→token(§3.13) + 吸收 4 共享样式 + AppFont
  → 出口门：winbuild build 绿 + 浅色视觉一致

Wave 1（4 并行 background fixer，Wave 0 出口后）:
  - fixer @ S: S1/S2/S3(§3.12)/S4(§3.14)/主题(§3.17)
  - fixer @ W: W1-W6(§3.9)/W8(§3.10)+单测/Messenger/删本地样式/hex/TargetNullValue :161/:182
  - fixer @ M: M1-M5(§3.15/§3.16)/Messenger/SplitRatio 清理/hex/TargetNullValue :212/:302/:324
  - fixer @ Q: QuickAdd hex(§3.13)
  → 出口门：build 绿 + grep 零残留

Wave 2: Linux dotnet test + winbuild 截图验收（§5 12 项）
```
