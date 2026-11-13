using System.Windows.Forms;

namespace CursorFade;

/// <summary>自检：生成角色分离淡化副本 → 临时替换 Arrow/IBeam/Hand → 1.2 秒后还原。返回 0 表示全部成功。</summary>
internal static class SelfTest
{
    public static int Run()
    {
        var items = new List<(uint Id, string FadedPath, string OriginalPath)>();
        foreach (var (id, regName) in CursorMetrics.Targets)
        {
            var src = CursorMetrics.GetCursorPath(regName);
            if (src == null)
            {
                Log.Write($"[自检] 未找到 {regName} 光标文件");
                return 1;
            }

            var faded = AniFader.EnsureFaded(src, 30);
            if (faded == null)
            {
                Log.Write($"[自检] 生成淡化文件失败: {regName}");
                return 2;
            }

            var probe = Native.LoadCursorFromFile(faded);
            if (probe == IntPtr.Zero)
            {
                Log.Write($"[自检] 淡化文件无法被系统加载: {faded}");
                return 3;
            }
            Native.DestroyCursor(probe);

            // 0%（完全隐藏角色）档位的可加载性同样验证
            var hidden = AniFader.EnsureFaded(src, 0);
            if (hidden == null)
            {
                Log.Write($"[自检] 生成 0% 隐藏文件失败: {regName}");
                return 2;
            }
            probe = Native.LoadCursorFromFile(hidden);
            if (probe == IntPtr.Zero)
            {
                Log.Write($"[自检] 0% 隐藏文件无法被系统加载: {hidden}");
                return 3;
            }
            Native.DestroyCursor(probe);

            items.Add((id, faded, src));
        }

        if (!CursorFader.Apply(items))
        {
            Log.Write("[自检] SetSystemCursor 失败");
            return 4;
        }

        Log.Write($"[自检] 已切换为 30% 角色淡化（Arrow/IBeam/Hand），共 {items.Count} 项；0% 档可加载性通过");
        Thread.Sleep(1200);

        CursorFader.Restore();
        Log.Write("[自检] 已还原");
        return 0;
    }
}
