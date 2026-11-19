using System;
using System.Threading;
using System.Windows.Automation;

namespace CursorFade;

/// <summary>
/// UI Automation 泛用 tooltip 探测：覆盖 Chromium/Electron/WinUI 等在页面内（DOM）
/// 绘制、没有独立窗口的 tooltip——QQ、Trae、WPS截图等窗口路线无法触及的场景。
/// 后台 STA 线程低频查询"前台窗口无障碍树中 ControlType=ToolTip 且正在显示"的元素，
/// 主循环读取最近 1 秒的命中。查询/异常都不触碰 UI 线程；个别应用 UIA 超时仅损失该轮。
/// </summary>
internal static class UiaTooltipProbe
{
    private static readonly object _lock = new();
    private static long _hitTick;
    private static string? _hitEvidence;
    private static Thread? _thread;
    private static int _diagCounter;

    public static void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(QueryLoop) { IsBackground = true, Name = "uia-tooltip" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>最近 1s 内命中过 UIA tooltip 则返回证据串，否则 null（主循环 80ms 轮询，低开销）。</summary>
    public static string? RecentEvidence()
    {
        lock (_lock)
        {
            if (_hitEvidence != null && Environment.TickCount64 - _hitTick <= 1000)
                return _hitEvidence;
            return null;
        }
    }

    private static void QueryLoop()
    {
        while (true)
        {
            try
            {
                QueryOnce();
            }
            catch (Exception ex)
            {
                // UIA 对个别应用会超时/抛异常：记一条限流诊断后继续
                if (++_diagCounter % 100 == 1)
                    Log.Write($"UIA查询异常(跳过本轮): {ex.GetType().Name}");
            }
            Thread.Sleep(350);
        }
    }

    private static void QueryOnce()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        var root = AutomationElement.FromHandle(hwnd);
        var tips = root.FindAll(TreeScope.Subtree,
            new PropertyCondition(AutomationElementIdentifiers.ControlTypeProperty, ControlType.ToolTip));
        if (tips.Count == 0) return;

        Native.GetCursorPos(out var pt);
        foreach (AutomationElement tip in tips)
        {
            System.Windows.Rect r;
            try { r = tip.Current.BoundingRectangle; }
            catch { continue; }
            if (r.IsEmpty || double.IsNaN(r.X) || double.IsInfinity(r.X)) continue; // 隐藏元素 rect 为空

            // 与光标距离（≤64 = 正压在指针处）
            int dx = Math.Max(0, Math.Max((int)r.Left - pt.X, pt.X - (int)r.Right));
            int dy = Math.Max(0, Math.Max((int)r.Top - pt.Y, pt.Y - (int)r.Bottom));
            if (dx > 64 || dy > 64) continue;

            string name = "";
            try { name = tip.Current.Name; } catch { }
            if (name.Length > 40) name = name[..40];
            lock (_lock)
            {
                _hitTick = Environment.TickCount64;
                _hitEvidence = $"UIA-ToolTip|{(int)r.Left},{(int)r.Top} {(int)r.Width}x{(int)r.Height}|\"{name}\"|uia";
            }
            return;
        }
    }
}
