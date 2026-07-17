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
