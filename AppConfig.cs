using System.Text.Json;
using Microsoft.Win32;

namespace CursorFade;

internal sealed class AppConfig
{
    public bool Enabled { get; set; } = true;
    public int Opacity { get; set; } = 30;
    public bool TextFieldFade { get; set; } = true;
    public bool FadeTransition { get; set; }
    public List<string> LearnedClasses { get; set; } = new();
    public List<string> ExcludeApps { get; set; } = new();

    public void LearnClass(string cls)
    {
        if (string.IsNullOrWhiteSpace(cls)) return;
        if (LearnedClasses.Any(c => c.Equals(cls, StringComparison.OrdinalIgnoreCase))) return;
        LearnedClasses.Add(cls);
        if (LearnedClasses.Count > 40) LearnedClasses.RemoveAt(0);
        Save();
    }

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CursorFade", "config.json");

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "CursorFade";

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null)
                {
                    cfg.Opacity = Math.Clamp(cfg.Opacity, 0, 100);
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"读取配置失败: {ex.Message}");
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write($"保存配置失败: {ex.Message}");
        }
    }

    public static bool IsAutoStartEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValue) != null;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? string.Empty;
                if (exe.Length > 0) key.SetValue(RunValue, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValue, false);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"设置开机自启失败: {ex.Message}");
        }
    }
}
