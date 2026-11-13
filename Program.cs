using System.Windows.Forms;

namespace CursorFade;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = SelfTest.Run();
            return;
        }

        using var mutex = new Mutex(true, @"Local\CursorFade.SingleInstance", out bool created);
        if (!created) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeRestore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => SafeRestore();

        Log.Write("启动");
        Application.Run(new TrayContext());
        SafeRestore();
    }

    private static void SafeRestore()
    {
        try
        {
            CursorFader.Restore();
        }
        catch
        {
            // 退出路径
        }
    }
}
