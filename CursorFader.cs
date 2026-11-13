namespace CursorFade;

internal static class CursorFader
{
    private static readonly List<(uint Id, string OriginalPath)> Applied = new();

    public static bool IsApplied => Applied.Count > 0;

    /// <summary>批量替换目标光标为淡化版；任一成功即视为开启。同 id 重复替换时以新记录为准。</summary>
    public static bool Apply(IReadOnlyList<(uint Id, string FadedPath, string OriginalPath)> items)
    {
        bool any = false;
        foreach (var (id, fadedPath, originalPath) in items)
        {
            try
            {
                var handle = Native.LoadCursorFromFile(fadedPath);
                if (handle == IntPtr.Zero) continue;
                if (Native.SetSystemCursor(handle, id))
                {
                    Applied.RemoveAll(a => a.Id == id);
                    Applied.Add((id, originalPath));
                    any = true;
                }
                else
                {
                    Native.DestroyCursor(handle);
                }
            }
            catch
            {
                // 单个光标失败不阻断其余
            }
        }
        return any;
    }

    /// <summary>按记录恢复所有已替换的光标；个别失败时整体重载光标方案兜底。</summary>
    public static void Restore()
    {
        if (Applied.Count == 0) return;
        bool fallback = false;
        foreach (var (id, originalPath) in Applied)
        {
            try
            {
                if (File.Exists(originalPath))
                {
                    var handle = Native.LoadCursorFromFile(originalPath);
                    if (handle != IntPtr.Zero && Native.SetSystemCursor(handle, id)) continue;
                    if (handle != IntPtr.Zero) Native.DestroyCursor(handle);
                }
                fallback = true;
            }
            catch
            {
                fallback = true;
            }
        }
        Applied.Clear();
        if (fallback) Native.SystemParametersInfo(Native.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
    }

    /// <summary>启动自愈：用注册表原图强制刷新所有目标光标，消除上次异常退出残留的淡化状态。</summary>
    public static void Heal()
    {
        foreach (var (id, regName) in CursorMetrics.Targets)
        {
            var path = CursorMetrics.GetCursorPath(regName);
            if (path == null) continue;
            try
            {
                var handle = Native.LoadCursorFromFile(path);
                if (handle == IntPtr.Zero) continue;
                if (!Native.SetSystemCursor(handle, id)) Native.DestroyCursor(handle);
            }
            catch
            {
                // 自愈尽力而为
            }
        }
    }
}
