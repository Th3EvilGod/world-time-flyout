using System.Text;
using WorldTime.Core;

namespace WorldTimeFlyout;

/// <summary>
/// Small rotating log in %LOCALAPPDATA%\WorldTimeFlyout\log.txt. Always records
/// errors; records calendar tracking details only with --diag (or when the
/// file "diag.on" exists next to the log) so a misbehaving PC can be debugged.
/// </summary>
public static class DiagLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly Lock Gate = new();
    private static string? _path;

    public static bool Verbose { get; set; }

    public static string Path => _path ??= System.IO.Path.Combine(SettingsStore.DefaultDirectory, "log.txt");

    public static void Init(bool verbose)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DefaultDirectory);
            Verbose = verbose || File.Exists(System.IO.Path.Combine(SettingsStore.DefaultDirectory, "diag.on"));
        }
        catch { /* logging must never break the app */ }
    }

    public static void Write(string message) => Append("    ", message);

    public static void Detail(string message)
    {
        if (Verbose) Append("  . ", message);
    }

    private static void Append(string prefix, string message)
    {
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Copy(Path, Path + ".1", overwrite: true);
                    File.Delete(Path);
                }
                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(prefix).Append(message).AppendLine();
                File.AppendAllText(Path, line.ToString());
            }
        }
        catch { /* ignore */ }
    }
}
