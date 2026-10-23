using System;
using System.IO;
using System.Text;

namespace Fable2Launcher;

/// <summary>Best-effort launcher startup crash log; never modifies game files.</summary>
internal static class LauncherStartupDiagnostics
{
    internal static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Fable2Launcher", "launcher-startup-error.log");

    internal static void Report(string operation, Exception error)
    {
        try
        {
            string path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:O}] {operation}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // Logging is advisory: never crash or recursively log.
        }
    }
}
