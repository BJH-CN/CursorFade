using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CursorFade;

internal sealed class TrayContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;

    private bool _faded;
    private long _lastHitTick;

    // 测试预览锁定：>0 时主循环挂起状态评估，保证完整显示 N 个 tick
    private int _testTicksRemaining;

    // 渐变过渡（独立 Timer 驱动，避免阻塞主循环）
    private readonly System.Windows.Forms.Timer _transTimer;
    private readonly Queue<int> _transSteps = new(); // 每步一个不透明度%；-1 = 还原原图

    // 预热状态：进行中时淡化触发走"等待"而非同步生成，避免打字瞬间卡顿
    private volatile bool _prewarmDone;

    // 淡化周期日志聚合：只在周期 ≥3s 时落一条，短抖动不记录
    private long _fadeStartTick;
    private string _fadeEvidence = string.Empty;
    private int _fadeFailCount;
    private bool _fadeFailLogged;

    // 同一会话内每类"跳过学习"只记一次，防止刷屏
    private readonly HashSet<string> _skipLearnLogged = new(StringComparer.OrdinalIgnoreCase);

    // 临时例外（本次运行）：不持久化，且目标程序退出即自动移除 —— 生命周期绑定目标进程
    private readonly HashSet<string> _sessionExcludes = new(StringComparer.OrdinalIgnoreCase);
    private int _sessionScanCounter;

    public TrayContext()
    {
        _config = AppConfig.Load();
        foreach (var cls in _config.LearnedClasses) ImeWatcher.LearnedClasses.Add(cls);

        // 上次异常退出可能留下淡化状态，启动时先用注册表原图自愈
        CursorFader.Heal();

        ImeWatcher.Install();

        _tray = new NotifyIcon
        {
            Icon = BuildIcon(),
            Visible = true,
            Text = "光标遮挡优化"
        };
        _tray.DoubleClick += (_, _) => ToggleEnabled();
        RebuildMenu();

        _tray.BalloonTipTitle = "光标遮挡优化已启动";
        _tray.BalloonTipText = "打字时输入法候选框压到指针会自动淡出。右键托盘图标可调强度或退出。";
        _tray.ShowBalloonTip(5000);

        _timer = new System.Windows.Forms.Timer { Interval = 80 };
        _timer.Tick += OnTick;
        _timer.Start();

        _transTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _transTimer.Tick += OnTransitionTick;

        Prewarm();
        UpdateTooltip();
    }

    // ---------- 预热 ----------

    private void Prewarm()
    {
        int opacity = _config.Opacity;
        int[] anchors = _config.FadeTransition
            ? BuildTransitionSteps(100, opacity).Where(v => v != opacity).ToArray()
            : Array.Empty<int>();
        _prewarmDone = false;
        Task.Run(() =>
        {
            try
            {
                // 先保证目标档（核心功能），再预热渐变锚点档（丝滑过渡）
                foreach (var (_, regName) in CursorMetrics.Targets)
                {
                    var src = CursorMetrics.GetCursorPath(regName);
                    if (src == null) continue;
                    try
                    {
                        AniFader.EnsureFaded(src, opacity);
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"预热失败({regName}): {ex.Message}");
                    }
                }
                foreach (var anchor in anchors)
                {
                    foreach (var (_, regName) in CursorMetrics.Targets)
                    {
                        var src = CursorMetrics.GetCursorPath(regName);
                        if (src == null) continue;
                        try
                        {
                            AniFader.EnsureFaded(src, anchor);
                        }
                        catch
                        {
                            // 锚点档预热失败只影响过渡档数，降级即可
                        }
                    }
                }
            }
            finally
            {
                _prewarmDone = true;
            }
        });
    }

    /// <summary>
    /// 生成渐变档位序列（ease-out：先快速接近目标、再缓缓收尾），共 5 步，不含起点。
    /// 终点值保证等于 to。
    /// </summary>
    private static int[] BuildTransitionSteps(int from, int to)
    {
        const int n = 5;
        var steps = new List<int>(n);
        for (int i = 1; i <= n; i++)
        {
            double t = i / (double)n;
            double e = 1 - (1 - t) * (1 - t);   // easeOutQuad
            int v = i == n ? to : (int)Math.Round(from + (to - from) * e);
            if (steps.Count == 0 || v != steps[^1]) steps.Add(v);
        }
        return steps.ToArray();
    }

    /// <summary>
    /// 收集指定不透明度的淡化光标项。
    /// available=false 表示预热未完成且缓存缺失（调用方应本轮跳过、下轮重试，不阻塞生成）。
    /// </summary>
    private (bool Available, List<(uint Id, string FadedPath, string OriginalPath)> Items) CollectFadedItems(int alpha)
    {
        var items = new List<(uint Id, string FadedPath, string OriginalPath)>();
        foreach (var (id, regName) in CursorMetrics.Targets)
        {
            var original = CursorMetrics.GetCursorPath(regName);
            if (original == null) continue;
            var faded = AniFader.GetCachedPath(original, alpha);
            if (faded == null)
            {
                if (!_prewarmDone) return (false, items); // 等预热
                faded = AniFader.EnsureFaded(original, alpha);
                if (faded == null) continue;
            }
            items.Add((id, faded, original));
        }
        return (true, items);
    }

    // ---------- 主循环 ----------

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            // 前台观测放最前：任何分支都不跳过，保证菜单打开时缓存足够新鲜
            var fg = ImeWatcher.ForegroundProcessName();

            // 临时例外的生命周期绑定"目标程序"：目标进程退出即自动移除（每 6 tick≈480ms 扫一次）
            if (_sessionExcludes.Count > 0 && ++_sessionScanCounter >= 6)
            {
                _sessionScanCounter = 0;
                foreach (var app in _sessionExcludes.ToList())
                {
                    if (!ImeWatcher.IsProcessRunning(app))
                    {
                        _sessionExcludes.Remove(app);
                        Log.Write($"临时例外移除「{app}」：目标程序已退出");
                    }
                }
            }

            if (_testTicksRemaining > 0)
            {
                _testTicksRemaining--;
                return; // 测试预览期间锁定状态
            }
            if (_transSteps.Count > 0) return; // 渐变过渡进行中，暂停评估
            if (!_config.Enabled)
            {
                BeginUnfade();
                return;
            }

            // 例外应用（永久 + 本次运行）：前台在任一名单内时不淡化
            if (fg.Length > 0 &&
                (_config.ExcludeApps.Any(x => x.Equals(fg, StringComparison.OrdinalIgnoreCase)) ||
                 _sessionExcludes.Contains(fg)))
            {
                BeginUnfade();
                return;
            }

            bool hit = ImeWatcher.CandidatePresent(out var evidence, out var learntClass);
            if (hit && learntClass != null)
            {
                if (ImeWatcher.IsLearneableClass(learntClass))
                {
                    _config.LearnClass(learntClass);
                    ImeWatcher.LearnedClasses.Add(learntClass);
                    Log.Write($"学习候选窗类名: {learntClass}");
                }
                else if (_skipLearnLogged.Add(learntClass))
                {
                    Log.Write($"跳过通用类名（不学习）: {learntClass}");
                }
            }

            // 淡化仅在"输入行为 + 指针位于输入框内"同时成立时发生：
            // - 候选窗近旁（吸附于输入点，光标被盖住变箭头也视为在输入框内）→ 淡
            // - 打字中且指针呈文本光标（悬停在输入框内）→ 淡
            // 鼠标移出输入框范围 → 立即恢复，不随打字计时残留
            // 打字计时窗 1200ms（回退到"完全符合期望"版本的参数）：
            // 更长的窗口会让"结束输入/挪走指针"后的退出时机变迟钝
            if (!hit && _config.TextFieldFade &&
                ImeWatcher.TypingRecently(1200) && !ImeWatcher.LeftButtonDown() &&
                ImeWatcher.IsTextCursor())
            {
                hit = true;
                evidence = "输入文字中(指针在输入框内)";
            }

            if (hit)
            {
                _lastHitTick = Environment.TickCount64;
                BeginFade(evidence);
            }
            else if (_faded && Environment.TickCount64 - _lastHitTick > 500)
            {
                // 轻微滞回，避免候选窗逐键刷新时指针反复闪动
                BeginUnfade();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"检测异常: {ex.Message}");
        }
    }

    // ---------- 淡入 / 淡出 ----------

    private void BeginFade(string evidence)
    {
        if (_faded) return;

        var (available, items) = CollectFadedItems(_config.Opacity);
        if (!available) return;          // 预热未完成：本轮跳过，绝不卡住打字
        if (items.Count == 0)
        {
            if (++_fadeFailCount >= 3 && !_fadeFailLogged)
            {
                _fadeFailLogged = true;
                Log.Write("淡化文件连续生成失败，本会话暂停尝试（重启后恢复）");
            }
            return;
        }
        _fadeFailCount = 0;
        _fadeFailLogged = false;

        _faded = true;
        _fadeStartTick = Environment.TickCount64;
        _fadeEvidence = evidence;
        UpdateTooltip();

        if (_config.FadeTransition)
        {
            // 原图(100%) → 目标：5 档 ease-out 渐进；StartTransition 会立即应用第一档
            StartTransition(new Queue<int>(BuildTransitionSteps(100, _config.Opacity)));
            return;
        }
        ApplyItems(items);
    }

    private void BeginUnfade()
    {
        if (!_faded) return;

        _faded = false;
        UpdateTooltip();
        FlushFadeLog();

        if (_config.FadeTransition)
        {
            // 目标 → 原图：末档 100% 即还原原图（-1），同样 5 档渐进
            var steps = BuildTransitionSteps(_config.Opacity, 100).ToList();
            if (steps[^1] == 100) steps[^1] = -1;
            else steps.Add(-1);
            StartTransition(new Queue<int>(steps));
            return;
        }
        StopTransition();
        CursorFader.Restore();
    }

    /// <summary>淡化周期 ≥3s 才落一条日志（含触发原因），短抖动/测试不污染日志。</summary>
    private void FlushFadeLog()
    {
        long duration = Environment.TickCount64 - _fadeStartTick;
        if (duration >= 3000)
            Log.Write($"淡化 {duration / 1000.0:F1}s <- {_fadeEvidence}");
    }

    private void ApplyItems(List<(uint Id, string FadedPath, string OriginalPath)> items)
        => CursorFader.Apply(items);

    // ---------- 渐变过渡 ----------

    private void StartTransition(Queue<int> steps)
    {
        StopTransition();
        while (steps.Count > 0) _transSteps.Enqueue(steps.Dequeue());
        AdvanceTransition();
        if (_transSteps.Count > 0) _transTimer.Start();
    }

    private void StopTransition()
    {
        _transSteps.Clear();
        _transTimer.Stop();
    }

    private void OnTransitionTick(object? sender, EventArgs e)
    {
        try
        {
            AdvanceTransition();
        }
        catch (Exception ex)
        {
            Log.Write($"过渡异常: {ex.Message}");
            StopTransition();
            CursorFader.Restore();
        }
    }

    private void AdvanceTransition()
    {
        if (_transSteps.Count == 0)
        {
            _transTimer.Stop();
            return;
        }

        int step = _transSteps.Dequeue();
        if (step < 0)
        {
            CursorFader.Restore();
        }
        else
        {
            var (ready, items) = CollectFadedItems(step);
            if (ready && items.Count > 0) ApplyItems(items);
            // 缺档时跳过该步直接进入下一步，保证过渡总能走完
        }

        if (_transSteps.Count == 0) _transTimer.Stop();
    }

    // ---------- 菜单动作 ----------

    private void ToggleEnabled()
    {
        _config.Enabled = !_config.Enabled;
        _config.Save();
        if (!_config.Enabled) BeginUnfade();
        RebuildMenu();
        UpdateTooltip();
    }

    private void SetOpacity(int percent)
    {
        _config.Opacity = percent;
        _config.Save();
        StopTransition();
        BeginUnfade();
        RebuildMenu();
        UpdateTooltip();
        Prewarm();
    }

    private void RebuildMenu()
    {
        var menu = new ContextMenuStrip();

        var enabled = new ToolStripMenuItem("启用遮挡淡化")
        {
            Checked = _config.Enabled,
            CheckOnClick = true
        };
        enabled.CheckedChanged += (_, _) =>
        {
            _config.Enabled = enabled.Checked;
            _config.Save();
            if (!_config.Enabled) BeginUnfade();
            UpdateTooltip();
        };
        menu.Items.Add(enabled);

        var opacity = new ToolStripMenuItem("淡出强度");
        (int Value, string Label)[] options =
        {
            (0, "0%（完全隐藏角色）"),
            (15, "15%"),
            (20, "20%"),
            (30, "30%"),
            (40, "40%"),
            (50, "50%"),
        };
        foreach (var (value, label) in options)
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = _config.Opacity == value
            };
            item.Click += (_, _) => SetOpacity(value);
            opacity.DropDownItems.Add(item);
        }
        menu.Items.Add(opacity);

        var transition = new ToolStripMenuItem("切换时渐变过渡")
        {
            Checked = _config.FadeTransition,
            CheckOnClick = true
        };
        transition.CheckedChanged += (_, _) =>
        {
            _config.FadeTransition = transition.Checked;
            _config.Save();
            Prewarm(); // 预热渐变中间档
        };
        menu.Items.Add(transition);

        var textField = new ToolStripMenuItem("输入且指针在输入框内时淡出")
        {
            Checked = _config.TextFieldFade,
            CheckOnClick = true
        };
        textField.CheckedChanged += (_, _) =>
        {
            _config.TextFieldFade = textField.Checked;
            _config.Save();
        };
        menu.Items.Add(textField);

        // 例外：临时（目标程序退出即失效）与永久（写入配置，重启仍生效）
        var sessionMenu = BuildExcludeMenu("临时例外（退出该程序即恢复）", session: true);
        menu.Items.Add(sessionMenu);
        var permaMenu = BuildExcludeMenu("永久例外（重启仍生效）", session: false);
        menu.Items.Add(permaMenu);

        var autoStart = new ToolStripMenuItem("开机自启")
        {
            Checked = AppConfig.IsAutoStartEnabled,
            CheckOnClick = true
        };
        autoStart.CheckedChanged += (_, _) => AppConfig.SetAutoStart(autoStart.Checked);
        menu.Items.Add(autoStart);

        menu.Items.Add(new ToolStripSeparator());

        var test = new ToolStripMenuItem("测试淡出效果（2 秒）");
        test.Click += (_, _) =>
        {
            StopTransition();
            if (_faded) // 先还原，保证能看到"进入淡出"的过程
            {
                _faded = false;
                CursorFader.Restore();
            }
            _lastHitTick = 0;
            BeginFade("手动测试");
            _testTicksRemaining = 25; // 25 × 80ms = 2s 内主循环不改状态
        };
        menu.Items.Add(test);

        var restore = new ToolStripMenuItem("立即还原光标（光标异常时优先尝试）");
        restore.Click += (_, _) =>
        {
            StopTransition();
            if (_faded)
            {
                _faded = false;
                FlushFadeLog();
            }
            CursorFader.Restore();
            _lastHitTick = 0;
        };
        menu.Items.Add(restore);

        var log = new ToolStripMenuItem("查看日志");
        log.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Log.FilePath}\"") { UseShellExecute = true });
            }
            catch
            {
                // 打开失败时忽略
            }
        };
        menu.Items.Add(log);

        menu.Items.Add(new ToolStripSeparator());

        var restart = new ToolStripMenuItem("重启本程序（状态异常时用）");
        restart.Click += (_, _) => RestartApp();
        menu.Items.Add(restart);

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(exit);

        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    /// <summary>
    /// 构建"例外"子菜单（临时/永久共用结构）：每次展开时实时识别当前前台应用并重建条目。
    /// session=true → 本次运行内生效，退出程序自动重置；false → 写入配置，重启仍生效。
    /// </summary>
    private ToolStripMenuItem BuildExcludeMenu(string title, bool session)
    {
        var menu = new ToolStripMenuItem(title);
        menu.DropDownOpening += (_, _) =>
        {
            menu.DropDownItems.Clear();

            var fg = ImeWatcher.ForegroundProcessName();
            bool hasList = session ? _sessionExcludes.Count > 0 : _config.ExcludeApps.Count > 0;

            if (fg.Length > 0)
            {
                bool inList = session
                    ? _sessionExcludes.Contains(fg)
                    : _config.ExcludeApps.Any(x => x.Equals(fg, StringComparison.OrdinalIgnoreCase));

                var toggle = new ToolStripMenuItem(session
                    ? (inList
                        ? $"✔ 「{fg}」临时不淡化 — 点击立即恢复"
                        : $"＋ 暂停在「{fg}」中淡化 — 退出该程序后自动恢复")
                    : (inList
                        ? $"✔ 「{fg}」已永久排除 — 点击恢复此应用"
                        : $"＋ 将「{fg}」加入永久例外 — 重启后仍然生效"));
                toggle.Click += (_, _) =>
                {
                    if (session)
                    {
                        if (inList)
                        {
                            _sessionExcludes.Remove(fg);
                            Log.Write($"临时例外手动移除「{fg}」");
                        }
                        else
                        {
                            _sessionExcludes.Add(fg);
                            Log.Write($"临时例外加入「{fg}」（退出该程序后自动恢复）");
                        }
                    }
                    else
                    {
                        if (inList)
                        {
                            _config.ExcludeApps.RemoveAll(x => x.Equals(fg, StringComparison.OrdinalIgnoreCase));
                            Log.Write($"永久例外移除「{fg}」");
                        }
                        else
                        {
                            if (!_config.ExcludeApps.Any(x => x.Equals(fg, StringComparison.OrdinalIgnoreCase)))
                                _config.ExcludeApps.Add(fg);
                            Log.Write($"永久例外加入「{fg}」");
                        }
                        _config.Save();
                    }
                };
                menu.DropDownItems.Add(toggle);
            }
            else
            {
                menu.DropDownItems.Add(new ToolStripMenuItem("（请先切换到目标窗口，再打开菜单）") { Enabled = false });
            }

            if (hasList)
            {
                menu.DropDownItems.Add(new ToolStripSeparator());
                menu.DropDownItems.Add(new ToolStripMenuItem(session
                    ? "这些应用临时不淡化（退出该程序即自动恢复，点击条目立即移除）："
                    : "这些应用永久不淡化（点击条目恢复）：") { Enabled = false });

                var list = session
                    ? _sessionExcludes.ToList()
                    : _config.ExcludeApps.ToList();
                foreach (var app in list)
                {
                    var item = new ToolStripMenuItem($"    {app}");
                    item.Click += (_, _) =>
                    {
                        if (session) _sessionExcludes.Remove(app);
                        else _config.ExcludeApps.RemoveAll(x => x.Equals(app, StringComparison.OrdinalIgnoreCase));
                        if (!session) _config.Save();
                    };
                    menu.DropDownItems.Add(item);
                }
            }
        };
        return menu;
    }

    private void UpdateTooltip()
    {
        string state = !_config.Enabled ? "已暂停" : _faded ? "淡出中" : "已启用";
        string text = $"光标遮挡优化 · {state} · {_config.Opacity}%";
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    /// <summary>
    /// 重启本程序：先还原光标，1 秒后由系统命令拉起新实例（等待本实例释放单实例锁），再退出自身。
    /// 用于卡死/状态诡异时的一键自恢复。
    /// </summary>
    private void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (exe == null)
        {
            Log.Write("重启失败：无法获取程序路径");
            return;
        }

        StopTransition();
        if (_faded)
        {
            _faded = false;
            FlushFadeLog();
        }
        CursorFader.Restore();

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c timeout /t 1 /nobreak >nul & start \"\" \"{exe}\"")
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Exception ex)
        {
            Log.Write($"重启失败: {ex.Message}");
            return;
        }

        Log.Write("重启本程序");
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        ImeWatcher.Uninstall();
        ExitThread();
    }

    private void ExitApplication()
    {
        StopTransition();
        if (_faded)
        {
            _faded = false;
            FlushFadeLog();
        }
        CursorFader.Restore();
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        ImeWatcher.Uninstall();
        Log.Write("退出");
        ExitThread();
    }

    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(Color.FromArgb(240, 118, 199, 255));
            g.FillEllipse(fill, 1, 1, 30, 30);
            using var pen = new Pen(Color.FromArgb(255, 16, 52, 92), 4)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawLine(pen, 16, 9, 16, 23);
            g.DrawLine(pen, 10, 9, 22, 9);
            g.DrawLine(pen, 10, 23, 22, 23);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
