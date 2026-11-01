using System.IO;
using System.Text;

namespace Fable2Launcher;

/// <summary>
/// One-time v22 stability migration. Repeated PubGames crash sessions showed forced
/// readback activity at 0x12704000 shortly before STATUS_HEAP_CORRUPTION. The
/// migration disables both experimental texture paths once, preserves a backup,
/// and never overrides a later explicit user choice after the marker is written.
/// </summary>
internal static class StabilityRecovery
{
    internal const string MarkerFileName = ".launcher-visual-recovery-v22.done";
    internal const string LogFileName = "launcher-visual-recovery-v22.log";

    internal static bool TryRestoreCompatibilityReadback(string configPath, out string message)
    {
        message = "";
        string directory = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
        string markerPath = Path.Combine(directory, MarkerFileName);
        string logPath = Path.Combine(directory, LogFileName);

        if (File.Exists(markerPath)) return false;
        // No file means there is nothing from v13-v17 to migrate. Mark this
        // first launch as examined, otherwise a user's later explicit GPU
        // selection would be silently undone when the launcher next starts.
        if (!File.Exists(configPath))
        {
            try
            {
                File.WriteAllText(markerPath,
                    "Fable II stability recovery v22: no configuration existed; future user choices are preserved.\n",
                    new UTF8Encoding(false));
            }
            catch (Exception ex) { LauncherStartupDiagnostics.Report("Stability recovery v22 marker", ex); }
            return false;
        }

        var log = new StringBuilder();
        log.AppendLine("Fable II launcher stability recovery v22");
        log.AppendLine("Config: " + configPath);
        try
        {
            Fable2ConfigValues values = Fable2ConfigFile.Read(configPath);
            HeroDogTextureMode mode = TextureModeSettings.Get(values);
            log.AppendLine($"Before: realtime_texture_morphing={values.RealtimeTextureMorphing}, hero_dog_texture_readback={values.HeroDogTextureReadback}, mode={mode}");

            if (mode == HeroDogTextureMode.DiagnosticOff)
            {
                log.AppendLine("No migration needed; both experimental texture paths are already disabled.");
                File.WriteAllText(markerPath,
                    "Fable II stability recovery v22: no-readback mode already active; future user choices are preserved.\n",
                    new UTF8Encoding(false));
                File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));
                return false;
            }

            string backupPath = configPath + ".stability-recovery-v22-backup";
            if (!File.Exists(backupPath)) File.Copy(configPath, backupPath, false);

            values.RealtimeTextureMorphing = false;
            values.HeroDogTextureReadback = false;
            Fable2ConfigFile.Write(configPath, values);

            string marker =
                "Fable II launcher stability recovery v22\n" +
                "Disabled GPU realtime morphing and forced hero/dog readback after repeated PubGames heap corruption.\n" +
                "Only realtime_texture_morphing and hero_dog_texture_readback were changed.\n";
            File.WriteAllText(markerPath, marker, new UTF8Encoding(false));
            log.AppendLine("After: realtime_texture_morphing=false, hero_dog_texture_readback=false, mode=DiagnosticOff");
            log.AppendLine("Backup: " + backupPath);
            log.AppendLine("SUCCESS");
            File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));

            message = "Для стабильности отключены экспериментальные GPU Morphing и Forced Readback. Исходный конфиг сохранён в резервной копии v22.";
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                log.AppendLine("FAILED: " + ex);
                File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));
            }
            catch { }
            LauncherStartupDiagnostics.Report("Stability recovery v22", ex);
            return false;
        }
    }
}
