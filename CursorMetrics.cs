using Microsoft.Win32;

namespace CursorFade;

/// <summary>当前生效的光标路径信息。</summary>
internal static class CursorMetrics
{
    /// <summary>
    /// 需要随输入状态淡出的光标类型：
    /// 输入时指针可能在文本光标(IBeam)、普通箭头(候选窗盖住指针时)、手型(链接)间切换，全部覆盖。
    /// </summary>
    public static readonly (uint Id, string RegName)[] Targets =
    {
        (Native.OCR_NORMAL, "Arrow"),
        (Native.OCR_IBEAM, "IBeam"),
        (Native.OCR_HAND, "Hand"),
    };

    public static string? GetCursorPath(string regName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors");
            var raw = key?.GetValue(regName) as string;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var expanded = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            return File.Exists(expanded) ? expanded : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? IBeamPath() => GetCursorPath("IBeam");
}
