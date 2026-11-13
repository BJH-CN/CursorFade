# ============================================================================
# 【用途声明 / Purpose】
# 本脚本是 CursorFade 的**开发期测试工具**，用于模拟一个输入法候选窗，
# 验证程序的输入法识别与淡化逻辑。它不是 CursorFade 程序运行所需的一部分。
#
# 行为说明：它会把 powershell.exe 复制为 %TEMP%\cf_ime_sim\weaselserver.exe
# 并由该副本启动自身——这是为了让进程名命中内置输入法规则表。
# 该"复制并重命名系统程序"的行为仅存在于本文件，普通使用者**无需运行本脚本**，
# 杀毒软件若对此报警属于预期，可将本文件加入信任或直接删除。
# ============================================================================
# 模拟"其他输入法"候选窗：自举一个进程名为 weaselserver（小狼毫，位于内置规则名单）的独立进程，
# 在光标旁弹出 370x96 置顶无边框窗，验证 CursorFade 的输入法识别（strict 规则路径）。
# 用法：powershell -ExecutionPolicy Bypass -File simulate-ime.ps1 [-Duration 10] [-CopyAs weaselserver]
param(
    [string]$CopyAs = "weaselserver",
    [int]$Duration = 10
)
$ErrorActionPreference = 'Stop'

# 1) 自举：当前进程名不是目标名 -> 复制 powershell.exe 为目标名并由副本重启本脚本
$curName = [System.Diagnostics.Process]::GetCurrentProcess().ProcessName
if ($curName -ne $CopyAs) {
    $dir = Join-Path $env:TEMP 'cf_ime_sim'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $target = Join-Path $dir "$CopyAs.exe"
    if (-not (Test-Path $target)) {
        Copy-Item "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe" $target -Force
    }
    Start-Process $target -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
        '-CopyAs', $CopyAs, '-Duration', $Duration
    )
    "relaunched as process '$CopyAs'"
    exit 0
}

# 2) 此时本进程名 = 目标名（如 weaselserver）-> 创建模拟候选窗
Add-Type -AssemblyName System.Windows.Forms

$f = New-Object Windows.Forms.Form
$f.FormBorderStyle = 'FixedToolWindow'
$f.TopMost = $true
$f.StartPosition = 'Manual'
$f.ShowInTaskbar = $false
$f.Size = New-Object Drawing.Size(370, 96)
$f.Text = ''

# 典型候选窗位置：光标右下方吸附
$pos = [System.Windows.Forms.Cursor]::Position
$f.Location = New-Object Drawing.Point(($pos.X + 30), ($pos.Y + 30))

# 画一点内容方便肉眼确认
$label = New-Object Windows.Forms.Label
$label.Dock = 'Fill'
$label.TextAlign = 'MiddleCenter'
$label.Font = New-Object Drawing.Font('Microsoft YaHei', 11)
$label.Text = "模拟候选窗（进程名: $CopyAs）`r`n1. 测试   2. 示例   3. 验证"
$f.Controls.Add($label)

$f.Show()

$timer = New-Object Windows.Forms.Timer
$timer.Interval = $Duration * 1000
$timer.Add_Tick({ $timer.Stop(); $f.Close() })
$timer.Start()

$f.Add_FormClosed({ [System.Windows.Forms.Application]::Exit() })
[System.Windows.Forms.Application]::Run()
"simulate-ime done process=$([System.Diagnostics.Process]::GetCurrentProcess().ProcessName)"
