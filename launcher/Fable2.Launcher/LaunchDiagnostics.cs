using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fable2Launcher;

/// <summary>Opt-in, local-only per-launch report. No saves, game data, dumps or network uploads.</summary>
public sealed class LaunchDiagnostics
{
    private const int MaxLogBytes = 32 * 1024 * 1024;
    private static readonly string[] ConfigNames =
        ["fable2_config.toml", "fable_2.toml", "launcher-settings.toml", "launcher-dualshock.txt", "fable2_build.json"];
    private readonly string _gameDirectory;
    private readonly Dictionary<string, (long size, long ticks)> _oldLogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _notes = new();
    private readonly Dictionary<string, object?> _report = new();
    public string DirectoryPath { get; }

    private LaunchDiagnostics(GameLaunchPlan plan, ProcessStartInfo info)
    {
        _gameDirectory = plan.WorkingDirectory;
        DirectoryPath = Path.Combine(_gameDirectory, "diagnostics",
            "session-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DirectoryPath);
        _report["report_version"] = 1;
        _report["launcher_patch"] = "texture-diagnostics-dualshock-v3";
        _report["started_utc"] = DateTime.UtcNow;
        _report["status"] = "prepared";
        _report["executable"] = plan.Executable;
        _report["game_data_root"] = plan.GameRoot;
        _report["working_directory"] = plan.WorkingDirectory;
        _report["arguments"] = info.ArgumentList.ToArray();
        _report["os"] = Environment.OSVersion.ToString();
        _report["architecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        _report["notes"] = _notes;
        File.WriteAllText(Path.Combine(DirectoryPath, "texture-request.txt"),
            TextureModeSettings.DescribeRequest(_gameDirectory, Fable2ConfigFile.Read(Path.Combine(_gameDirectory, "fable2_config.toml"))));
        CaptureConfigs("config-start");
        var binaries = new Dictionary<string, object>();
        foreach (string name in new[] { "fable_2.exe", "rexruntime.dll", "rexgpu-xenos.dll", "rexgpu-xenos-vulkan.dll" })
        {
            string path = Path.Combine(_gameDirectory, name);
            if (!File.Exists(path)) continue;
            try
            {
                using var stream = File.OpenRead(path);
                binaries[name] = new { size = stream.Length, sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _notes.Add($"Hash {name}: {e.Message}"); }
        }
        _report["binary_hashes"] = binaries;
        string logs = Path.Combine(_gameDirectory, "logs");
        if (Directory.Exists(logs))
            foreach (string path in Directory.EnumerateFiles(logs, "*.log"))
            {
                var file = new FileInfo(path);
                _oldLogs[file.Name] = (file.Length, file.LastWriteTimeUtc.Ticks);
            }
        SaveReport();
    }

    public static LaunchDiagnostics Begin(GameLaunchPlan plan, ProcessStartInfo info) => new(plan, info);

    public void MarkStarted(int pid)
    {
        _report["process_id"] = pid;
        _report["status"] = "running";
        SaveReport();
    }

    public void Complete(int? exitCode, string? observationError = null)
    {
        _report["ended_utc"] = DateTime.UtcNow;
        _report["exit_code"] = exitCode;
        _report["exit_code_hex"] = exitCode is null ? null : FormatExitCode(exitCode.Value);
        _report["status"] = exitCode is null ? "observation_failed" : "process_exited";
        _report["observation_error"] = observationError;
        CaptureConfigs("config-end");
        var copiedLogs = new List<string>();
        var evidence = new List<string> { "Runtime log excerpts; absence of a line is not proof that a feature is disabled." };
        string logs = Path.Combine(_gameDirectory, "logs");
        try
        {
            if (Directory.Exists(logs))
            {
                var changed = Directory.EnumerateFiles(logs, "*.log").Select(p => new FileInfo(p))
                    .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 &&
                        (!_oldLogs.TryGetValue(f.Name, out var old) || old.size != f.Length || old.ticks != f.LastWriteTimeUtc.Ticks))
                    .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
                if (changed.Length > 8) _notes.Add("Only 8 most recently modified logs copied; rotation may include a prior session.");
                foreach (var file in changed.Take(8))
                {
                    try
                    {
                        string target = Path.Combine(DirectoryPath, "logs", file.Name);
                        CopyBounded(file.FullName, target, MaxLogBytes);
                        copiedLogs.Add(file.Name);
                        foreach (string line in File.ReadLines(target))
                        {
                            if (evidence.Count >= 200) break;
                            if (line.Contains("[texture-runtime]", StringComparison.Ordinal) ||
                                line.Contains("[texture-mode]", StringComparison.Ordinal) ||
                                line.Contains("[texture-morph]", StringComparison.Ordinal) ||
                                line.Contains("hero_dog_texture_readback", StringComparison.Ordinal))
                                evidence.Add(file.Name + ": " + line);
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _notes.Add($"Log {file.Name}: {e.Message}"); }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _notes.Add($"Log listing: {e.Message}"); }
        _report["log_files"] = copiedLogs;
        File.WriteAllLines(Path.Combine(DirectoryPath, "texture-runtime.txt"), evidence);
        SaveReport();
    }

    public static string FormatExitCode(int code) => $"0x{unchecked((uint)code):X8}";

    private void CaptureConfigs(string folder)
    {
        foreach (string name in ConfigNames)
        {
            string source = Path.Combine(_gameDirectory, name);
            if (!File.Exists(source)) continue;
            try
            {
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) continue;
                if (new FileInfo(source).Length > 2 * 1024 * 1024) { _notes.Add($"Config {name} too large; skipped."); continue; }
                CopyBounded(source, Path.Combine(DirectoryPath, folder, name), 2 * 1024 * 1024);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _notes.Add($"Config {name}: {e.Message}"); }
        }
    }

    private void CopyBounded(string source, string target, int maxBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long size = input.Length;
        if (size > maxBytes) { input.Position = size - maxBytes; _notes.Add($"Tail only ({maxBytes} bytes): {Path.GetFileName(source)}"); }
        using var output = File.Create(target);
        byte[] buffer = new byte[81920];
        int remaining = (int)Math.Min(size, maxBytes);
        while (remaining > 0)
        {
            int count = input.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (count == 0) break;
            output.Write(buffer, 0, count); remaining -= count;
        }
    }

    private void SaveReport()
    {
        string path = Path.Combine(DirectoryPath, "session.json");
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }
}
