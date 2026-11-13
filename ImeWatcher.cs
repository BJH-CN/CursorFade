using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CursorFade;

/// <summary>判断是否有输入法候选窗压在鼠标指针（画面）上。</summary>
internal static class ImeWatcher
{
    private static long _lastKeyTick;
    private static Native.LowLevelKeyboardProc? _proc;
    private static IntPtr _hook;
    private static readonly Dictionary<uint, string> ProcessNames = new();
    private static readonly object CacheLock = new();

    private static readonly HashSet<string> ImeProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chsime", "textinputhost", "ctfmon", "sogouimebroker", "sgim_broker", "sgtool",
        "qqpy", "qqpinyin", "qqpinyinconfig", "baiduime", "baiduimebroker", "weaselserver",
        "wechatime", "imebroker", "imeapp", "pinyin"
    };

    private static readonly string[] ImeClassHints =
    {
        "candidate", "sogoupy", "qqpinyin", "weasel", "msime", "microsoft.ime", "baiduime", "imebroker",
        "uicand", "wetype", "cheime"
    };

    /// <summary>运行中学习到的候选窗类名（由 TrayContext 从配置同步）。</summary>
    public static readonly HashSet<string> LearnedClasses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 过于通用、绝不能学习为候选窗的类名（学到会导致任意窗口误触发淡出）。
    /// </summary>
    private static readonly HashSet<string> NeverLearnClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chrome_WidgetWin_1", "Chrome_WidgetWin_0",
        "Xaml_WindowedPopupClass", "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow",
        "#32770", "PopupHost"
    };

    public static bool IsLearneableClass(string cls)
        => cls.Length > 0 && !NeverLearnClasses.Contains(cls) && !cls.StartsWith("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "tooltips_class32", "sysshadow", "#32768", "progman", "workerw", "shell_traywnd"
    };

    public static void Install()
    {
        try
        {
            _proc = HookCallback;
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
                Log.Write("键盘钩子安装失败，将仅依赖进程/类名规则识别候选窗");
        }
        catch (Exception ex)
        {
            Log.Write($"键盘钩子异常: {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        try
        {
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        catch
        {
            // 退出路径
        }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 &&
            (wParam == (IntPtr)Native.WM_KEYDOWN || wParam == (IntPtr)Native.WM_SYSKEYDOWN))
        {
            var kb = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (IsTypingKey(kb.vkCode))
                Interlocked.Exchange(ref _lastKeyTick, Environment.TickCount64);
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 判定"文字/选词输入"键：字母、数字、空格、退格、OEM 标点、IME 处理键。
    /// 排除 Ctrl/Alt/Win 组合（复制粘贴）与方向键、回车、Tab 等非输入键，
    /// 保证复制粘贴、方向选字等操作不误触发淡出。
    /// </summary>
    private static bool IsTypingKey(uint vk)
    {
        // 按着修饰键的组合键一律不算打字
        if ((Native.GetAsyncKeyState(0x11) & 0x8000) != 0) return false; // Ctrl
        if ((Native.GetAsyncKeyState(0x12) & 0x8000) != 0) return false; // Alt
        if ((Native.GetAsyncKeyState(0x5B) & 0x8000) != 0) return false; // LWin
        if ((Native.GetAsyncKeyState(0x5C) & 0x8000) != 0) return false; // RWin

        return (vk >= 0x30 && vk <= 0x39)          // 数字
            || (vk >= 0x41 && vk <= 0x5A)          // 字母
            || vk == 0x20                          // 空格（选词）
            || vk == 0x08                          // 退格（删拼音）
            || (vk >= 0xBA && vk <= 0xBF)          // OEM ;:'"[
            || vk == 0xC0 || vk == 0xDE            // OEM `'
            || vk == 0xBB || vk == 0xBD            // OEM + -（翻页）
            || vk == 0xE5;                         // VK_PROCESSKEY（IME 出字）
    }

    public static bool LeftButtonDown()
        => (Native.GetAsyncKeyState(0x01) & 0x8000) != 0;

    /// <summary>指定进程名（任意大小写）是否有实例在运行。</summary>
    public static bool IsProcessRunning(string nameLower)
    {
        foreach (var p in Process.GetProcesses())
        {
            bool match = false;
            try
            {
                match = p.ProcessName.Equals(nameLower, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 无权限/已退出的进程，忽略
            }
            p.Dispose();
            if (match) return true;
        }
        return false;
    }

    /// <summary>指针当前是否显示为文本光标（悬停在输入框/可编辑区域内）。</summary>
    public static bool IsTextCursor()
    {
        try
        {
            var info = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
            if (!Native.GetCursorInfo(ref info)) return false;
            if ((info.flags & Native.CURSOR_SHOWING) == 0) return false;
            var ibeam = Native.LoadCursor(IntPtr.Zero, (IntPtr)Native.OCR_IBEAM);
            return ibeam != IntPtr.Zero && info.hCursor == ibeam;
        }
        catch
        {
            return false;
        }
    }

    public static bool TypingRecently(int milliseconds)
        => Environment.TickCount64 - Interlocked.Read(ref _lastKeyTick) < milliseconds;

    /// <summary>光标点周围的大致范围（距离容忍 600px，无需精确热点/尺寸）。</summary>
    public static Native.RECT CursorBox()
    {
        Native.GetCursorPos(out var pt);
        const int half = 40;
        return new Native.RECT
        {
            Left = pt.X - half,
            Top = pt.Y - half,
            Right = pt.X + half,
            Bottom = pt.Y + half
        };
    }

    private static uint _lastFgPid;
    private static string _lastFgName = string.Empty;

    /// <summary>桌面/任务栏/托盘溢出面板/弹出菜单等"非应用"窗口类：不算有效的前台目标。</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Progman", "WorkerW", "SysListView32", "#32768",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Xaml_WindowedPopupClass", "Shell_Flyout"
    };

    /// <summary>
    /// 前台（焦点）进程名（小写、无扩展名）。
    /// 自身窗口（如右键菜单弹出时）与桌面/任务栏窗口不更新缓存，
    /// 始终返回"最近一次真实应用"的进程名，保证打开菜单时能正确对应目标应用。
    /// </summary>
    public static string ForegroundProcessName()
    {
        try
        {
            var hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return _lastFgName;

            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return _lastFgName; // 自身菜单窗口

            var cls = ClassNameOf(hwnd);
            if (ShellClasses.Contains(cls)) return _lastFgName; // 桌面/任务栏

            if (pid != _lastFgPid)
            {
                _lastFgPid = pid;
                _lastFgName = GetProcessName(pid);
            }
            return _lastFgName;
        }
        catch
        {
            return _lastFgName;
        }
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// 查找当前可见的输入法候选/预输入窗（候选窗吸附于输入点，距光标 600px 内视为命中）。
    /// learntClass 返回本次靠启发式识别出的窗口类名，便于后续固化为规则。
    /// </summary>
    public static bool CandidatePresent(out string evidence, out string? learntClass)
    {
        string found = string.Empty;
        string? learnt = null;
        bool typingRecently = Environment.TickCount64 - Interlocked.Read(ref _lastKeyTick) < 4000;
        var cursorBox = CursorBox();
        uint selfPid = (uint)Environment.ProcessId;

        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            if (!Native.GetWindowRect(hwnd, out var r)) return true;
            if (r.Width <= 0 || r.Height <= 0) return true;
            // 排除"占满其所在显示器"的窗口（输入法宿主的全屏透明窗）；
            // 长候选框变宽（可达上千 px）不会被误杀，多显示器下也按各自屏幕判定
            if (CoversMonitor(hwnd, r)) return true;

            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == selfPid || pid == 0) return true;

            var sb = new StringBuilder(256);
            Native.GetClassName(hwnd, sb, sb.Capacity);
            string cls = sb.ToString();
            if (ExcludedClasses.Contains(cls)) return true;

            string proc = GetProcessName(pid);
            bool strict = IsImeProcess(proc) || IsBuiltinImeClass(cls) ||
                          // 学习来的类名可信度较低：仅在正在打字时生效，
                          // 防止常驻小窗（皮肤伴侣、IDE 提示窗等）被误学后鼠标一靠近就触发
                          (LearnedClasses.Contains(cls) && typingRecently);

            // 距离圈回到 420px（600px 会让输入框外围一大片都被判"过近"）
            bool intersects = Intersects(r, cursorBox);
            bool near = intersects || RectDistance(r, cursorBox) <= 420;
            // 上下文门：仅当 指针在输入框内(IBeam) / 指针正被候选窗盖住 / 1.2 秒内敲过键 时才生效，
            // 鼠标停在非文本区域且没在打字 → 不因"过近"淡化
            bool allowed = IsTextCursor() || intersects || typingRecently;

            if (strict && near && allowed)
            {
                found = $"{cls}|{proc}|{r.Left},{r.Top} {r.Width}x{r.Height}|rule";
                return false;
            }

            // 未知输入法的兜底：刚打过字 + 小弹出窗 + 位于指针附近
            if (!strict && typingRecently && near && LooksLikeCandidatePopup(hwnd, r))            {
                found = $"{cls}|{proc}|{r.Left},{r.Top} {r.Width}x{r.Height}|heuristic";
                learnt = cls;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        evidence = found;
        learntClass = learnt;
        return found.Length > 0;
    }

    /// <summary>窗口是否覆盖其所在显示器 ≥90% 的面积。</summary>
    private static bool CoversMonitor(IntPtr hwnd, Native.RECT r)
    {
        var mon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return false;
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return false;

        long interW = Math.Min(r.Right, mi.rcMonitor.Right) - Math.Max(r.Left, mi.rcMonitor.Left);
        long interH = Math.Min(r.Bottom, mi.rcMonitor.Bottom) - Math.Max(r.Top, mi.rcMonitor.Top);
        if (interW <= 0 || interH <= 0) return false;
        long monArea = (long)mi.rcMonitor.Width * mi.rcMonitor.Height;
        if (monArea <= 0) return false;
        return interW * interH * 10 >= monArea * 9;
    }

    private static bool Intersects(Native.RECT a, Native.RECT b)
        => a.Left <= b.Right && a.Right >= b.Left && a.Top <= b.Bottom && a.Bottom >= b.Top;

    private static int RectDistance(Native.RECT a, Native.RECT b)
    {
        int dx = Math.Max(0, Math.Max(b.Left - a.Right, a.Left - b.Right));
        int dy = Math.Max(0, Math.Max(b.Top - a.Bottom, a.Top - b.Bottom));
        return Math.Max(dx, dy);
    }

    private static bool LooksLikeCandidatePopup(IntPtr hwnd, Native.RECT r)
    {
        // 真实候选窗 ≥120×40（含拼音行+候选行）；22×22 边角挂件、78×78 悬浮球等一律出局
        if (r.Width < 120 || r.Height < 40 || r.Width > 900 || r.Height > 500) return false;

        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();

        bool hasCaption = (style & Native.WS_CAPTION) == Native.WS_CAPTION;
        bool topmost = (ex & Native.WS_EX_TOPMOST) != 0;
        bool toolWindow = (ex & Native.WS_EX_TOOLWINDOW) != 0;
        return !hasCaption && (topmost || toolWindow);
    }

    private static bool IsImeProcess(string name)
    {
        if (name.Length == 0) return false;
        if (ImeProcesses.Contains(name)) return true;
        return name.Contains("pinyin", StringComparison.Ordinal) ||
               name.Contains("sogou", StringComparison.Ordinal) ||
               name.Contains("baiduime", StringComparison.Ordinal);
    }

    /// <summary>内置的输入法类名特征（含 "IME" 本身），可信、无打字门槛。</summary>
    private static bool IsBuiltinImeClass(string cls)
    {
        if (cls.Length == 0) return false;
        if (cls.Equals("IME", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var hint in ImeClassHints)
        {
            if (cls.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string GetProcessName(uint pid)
    {
        lock (CacheLock)
        {
            if (ProcessNames.TryGetValue(pid, out var cached)) return cached;
        }

        string name = string.Empty;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName.ToLowerInvariant();
        }
        catch
        {
            // 进程可能已退出或无权限
        }

        lock (CacheLock)
        {
            if (ProcessNames.Count > 512) ProcessNames.Clear();
            ProcessNames[pid] = name;
        }
        return name;
    }
}
