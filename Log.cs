namespace CursorFade;

internal static class Log
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CursorFade", "log.txt");

    private const int MaxBytes = 256 * 1024;
    private const int KeepTailBytes = 96 * 1024;

    private static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    // 只裁掉开头的旧内容，保留尾部最近的日志（不再整文件删除，
                    // 避免低频重要记录被高频状态日志冲掉）
                    var bytes = File.ReadAllBytes(FilePath);
                    var tail = new byte[KeepTailBytes];
                    Buffer.BlockCopy(bytes, bytes.Length - KeepTailBytes, tail, 0, KeepTailBytes);
                    File.WriteAllBytes(FilePath, tail);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }
    }
}
