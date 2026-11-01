using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Fable2Launcher;

/// <summary>
/// Opt-in, local-only diagnostic report. Does not modify the Windows registry,
/// saves or game files. Reads supported HKLM Windows Error Reporting LocalDumps
/// settings for the launched game and reports real crash dumps separately from
/// the shareable ZIP (dumps can contain private process memory).
/// </summary>
public sealed class LaunchDiagnostics
{
    private const int MaxLogBytes = 32 * 1024 * 1024;
    private static readonly string[] ConfigNames =
        ["fable2_config.toml", "fable_2.toml", "launcher-settings.toml", "launcher-dualshock.txt", "fable2_build.json", "pubgames-original-prompts.txt"];
    private static readonly string[] PromptLogs =
        ["launcher-pubgames-prompts.log", "launcher-keyboard-prompts.log", "launcher-dualshock-patch.log", "launcher-dualshock-menu-v17.log"];
    private readonly string _gameDirectory;
    private readonly string _gameDataRoot;
    private readonly string _executable;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly Dictionary<string, (long size, long ticks)> _oldLogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _notes = new();
    private readonly Dictionary<string, object?> _report = new();
    private readonly HashSet<string> _capturedFiles = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _liveTraceCancellation;
    private Task? _liveTraceTask;
    private bool _completed;
    private int? _pid;
    private readonly WerDumpConfiguration _werDump;
    public string DirectoryPath { get; }
    public string? ArchivePath { get; private set; }
    public string? ArchiveError { get; private set; }
    public string ResultPath => ArchivePath ?? DirectoryPath;
    public bool WerRegistryConfigured => _werDump.Configured;
    public bool CrashDumpDetected => _report.TryGetValue("crash_dump_detected", out object? value) && value is true;

    private LaunchDiagnostics(GameLaunchPlan plan, ProcessStartInfo info, ControllerPromptMode controllerMode,
        string controllerStatus, string pubGamesStatus)
    {
        _gameDirectory = Path.GetFullPath(plan.WorkingDirectory);
        _gameDataRoot = Path.GetFullPath(plan.GameRoot);
        _executable = Path.GetFullPath(plan.Executable);
        DirectoryPath = Path.Combine(_gameDirectory, "diagnostics",
            "session-" + _startedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DirectoryPath);
        _werDump = WerDumpConfiguration.Detect(_executable, _notes);
        _report["report_version"] = 6;
        _report["launcher_patch"] = "pubgames-diagnostics-v23-wer-fix";
        _report["selected_controller_prompt_mode"] = controllerMode.ToString();
        _report["controller_patch_status"] = controllerStatus;
        _report["pubgames_patch_status"] = pubGamesStatus;
        // Registry configuration is NOT proof of a dump. See "crash_dump_detected".
        _report["wer_registry_configured"] = _werDump.Configured;
        _report["wer_status"] = _werDump.Status;
        _report["wer_registry_key"] = _werDump.RegistryPath;
        _report["wer_dump_folder"] = _werDump.Folder;
        _report["wer_dump_type"] = _werDump.DumpType;
        _report["crash_dump_detected"] = false;
        _report["crash_dump_in_zip"] = false;
        _report["started_utc"] = _startedUtc;
        _report["status"] = "prepared";
        _report["archive_status"] = "pending";
        _report["executable"] = _executable;
        _report["game_data_root"] = _gameDataRoot;
        _report["working_directory"] = _gameDirectory;
        _report["arguments"] = info.ArgumentList.ToArray();
        _report["os"] = Environment.OSVersion.ToString();
        _report["architecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        _report["notes"] = _notes;
        WriteText("texture-request.txt", TextureModeSettings.DescribeRequest(_gameDirectory,
            Fable2ConfigFile.Read(Path.Combine(_gameDirectory, "fable2_config.toml"))));
        CaptureConfigs("config-start");
        CapturePromptFiles("prompts-start");
        var binaries = new Dictionary<string, object>();
        foreach (string name in new[] { "Fable2Launcher.exe", "fable_2.exe", "rexruntime.dll", "rexgpu-xenos.dll", "rexgpu-xenos-vulkan.dll" })
        {
            string path = Path.Combine(_gameDirectory, name);
            if (!File.Exists(path)) continue;
            try { binaries[name] = HashFile(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _notes.Add($"Hash {name}: {e.Message}"); }
        }
        _report["binary_hashes"] = binaries;
        string logs = Path.Combine(_gameDirectory, "logs");
        try
        {
            if (Directory.Exists(logs))
                foreach (string path in Directory.EnumerateFiles(logs, "*.log"))
                {
                    var file = new FileInfo(path);
                    _oldLogs[file.Name] = (file.Length, file.LastWriteTimeUtc.Ticks);
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _notes.Add("Initial log listing: " + e.Message); }
        SaveReport();
    }

    public static LaunchDiagnostics Begin(GameLaunchPlan plan, ProcessStartInfo info, ControllerPromptMode controllerMode,
        string controllerStatus, string pubGamesStatus) => new(plan, info, controllerMode, controllerStatus, pubGamesStatus);

    public void MarkStarted(int pid)
    {
        if (_completed) return;
        _pid = pid;
        _report["process_id"] = pid;
        _report["launcher_version"] = typeof(LaunchDiagnostics).Assembly.GetName().Version?.ToString();
        _report["status"] = "running";
        // Register output before starting the background writer to avoid a race
        // between HashSet enumeration during ZIP creation and live tracing.
        _capturedFiles.Add(Path.Combine(DirectoryPath, "live-trace.log"));
        _capturedFiles.Add(Path.Combine(DirectoryPath, "runtime-live.log"));
        var cancellation = new CancellationTokenSource();
        _liveTraceCancellation = cancellation;
        // Capture the token locally: Complete() can clear the field before
        // Task.Run actually starts when the process crashes immediately.
        CancellationToken token = cancellation.Token;
        _liveTraceTask = Task.Run(() => RunLiveTraceAsync(pid, token));
        SaveReport();
    }

    public void Complete(int? exitCode, string? observationError = null)
    {
        // Do not overwrite a completed crash report if a later UI operation fails.
        if (_completed) return;
        _report["ended_utc"] = DateTime.UtcNow;
        _report["exit_code"] = exitCode;
        _report["exit_code_hex"] = exitCode is null ? null : FormatExitCode(exitCode.Value);
        _report["status"] = exitCode is null ? "observation_failed" : "process_exited";
        _report["observation_error"] = observationError;
        StopLiveTrace();
        try
        {
            CaptureBestEffort("Final configuration", () => CaptureConfigs("config-end"));
            CaptureBestEffort("Final prompt state", () => CapturePromptFiles("prompts-end"));
            CaptureBestEffort("Native runtime logs", CaptureRuntimeLogs);
            if (exitCode is not null && exitCode != 0)
            {
                CaptureBestEffort("Windows application events", CaptureWindowsCrashEvents);
                CaptureBestEffort("WER crash dump", CaptureCrashDumps);
            }
            bool dumpFound = _report.TryGetValue("crash_dump_detected", out object? captured) && captured is true;
            WriteText("crash-summary.txt", DescribeExit(exitCode) + Environment.NewLine +
                (dumpFound
                    ? "A WER crash dump was found OUTSIDE this shareable ZIP. See crash-dump-status.txt." + Environment.NewLine
                    : "No WER crash dump was found. Exit code and module alone cannot identify the source." + Environment.NewLine) +
                "WER registry configuration: " + _werDump.Status + ". Launcher did not modify registry settings." + Environment.NewLine +
                "To enable supported HKLM LocalDumps, run the optional setup script with administrator consent." + Environment.NewLine +
                "Crash dumps can contain private process memory; review them before sharing.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            _notes.Add("Crash summary could not be written: " + e.Message);
        }
        finally
        {
            try { SaveReport(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _notes.Add("Report write: " + e.Message); }
            CreateArchive();
            _completed = true;
        }
    }

    private void CaptureBestEffort(string label, Action action)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or XmlException or InvalidOperationException or Win32Exception or AggregateException)
        { _notes.Add(label + " could not be collected: " + e.Message); }
    }

    private void StopLiveTrace()
    {
        try { _liveTraceCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        try
        {
            if (_liveTraceTask is not null && !_liveTraceTask.IsCompleted &&
                !_liveTraceTask.Wait(TimeSpan.FromSeconds(10)))
            {
                _notes.Add("Live tracing did not stop within 10 seconds; trace may be incomplete and is kept outside the ZIP.");
                // The writer still owns the files. Do not race ZIP SHA-256
                // verification against writes by a background task.
                _capturedFiles.Remove(Path.Combine(DirectoryPath, "live-trace.log"));
                _capturedFiles.Remove(Path.Combine(DirectoryPath, "runtime-live.log"));
            }
        }
        catch (AggregateException e)
        {
            _notes.Add("Live trace stop: " + (e.InnerException?.Message ?? e.Message));
        }
        finally
        {
            _liveTraceCancellation?.Dispose();
            _liveTraceCancellation = null;
            _liveTraceTask = null;
        }
    }

    private async Task RunLiveTraceAsync(int pid, CancellationToken cancellationToken)
    {
        string tracePath = Path.Combine(DirectoryPath, "live-trace.log");
        string runtimeTailPath = Path.Combine(DirectoryPath, "runtime-live.log");
        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _oldLogs)
            offsets[pair.Key] = pair.Value.size;
        string? lastModules = null;
        DateTime nextModuleSnapshot = DateTime.MinValue;
        try
        {
            using var trace = new StreamWriter(new FileStream(tracePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            using var runtime = new StreamWriter(new FileStream(runtimeTailPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            trace.WriteLine("# Fable II diagnostic live trace v23");
            trace.WriteLine("# UTC timestamp | process metrics | module changes | log activity");
            trace.WriteLine($"{DateTime.UtcNow:O} trace-start pid={pid}");
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using Process process = Process.GetProcessById(pid);
                    process.Refresh();
                    string exit = process.HasExited ? " exited=true" : string.Empty;
                    trace.WriteLine($"{DateTime.UtcNow:O} process ws={process.WorkingSet64} private={process.PrivateMemorySize64} virtual={process.VirtualMemorySize64} threads={process.Threads.Count} handles={SafeHandleCount(process)} cpu_ms={SafeCpuMilliseconds(process)}{exit}");
                    if (process.HasExited) break;
                    if (DateTime.UtcNow >= nextModuleSnapshot)
                    {
                        nextModuleSnapshot = DateTime.UtcNow.AddSeconds(2);
                        string modules = DescribeModules(process);
                        if (!string.Equals(lastModules, modules, StringComparison.Ordinal))
                        {
                            trace.WriteLine($"{DateTime.UtcNow:O} modules-change");
                            foreach (string line in modules.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                                trace.WriteLine("  " + line.TrimEnd('\r'));
                            lastModules = modules;
                        }
                    }
                }
                catch (ArgumentException)
                {
                    trace.WriteLine($"{DateTime.UtcNow:O} process-no-longer-present");
                    break;
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    trace.WriteLine($"{DateTime.UtcNow:O} process-snapshot-error {e.GetType().Name}: {e.Message}");
                }

                CaptureLiveLogDelta(trace, runtime, offsets);
                try { await Task.Delay(500, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            // Always read the final log tail, including when cancellation was
            // requested immediately after WaitForExitAsync completed.
            CaptureLiveLogDelta(trace, runtime, offsets);
            trace.WriteLine($"{DateTime.UtcNow:O} trace-stop");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _notes.Add("Live trace: " + e.Message);
        }
    }

    private void CaptureLiveLogDelta(StreamWriter trace, StreamWriter runtime, Dictionary<string, long> offsets)
    {
        string logs = Path.Combine(_gameDirectory, "logs");
        if (!Directory.Exists(logs)) return;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(logs, "*.log").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            trace.WriteLine($"{DateTime.UtcNow:O} log-list-error {e.Message}");
            return;
        }
        foreach (string path in files)
        {
            try
            {
                var file = new FileInfo(path);
                long offset = offsets.TryGetValue(file.Name, out long old) ? old : 0;
                if (file.Length < offset) offset = 0;
                if (file.Length == offset) continue;
                long available = file.Length - offset;
                long read = Math.Min(available, 1024 * 1024);
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                input.Position = offset;
                byte[] buffer = new byte[read];
                int count = input.Read(buffer, 0, buffer.Length);
                offsets[file.Name] = offset + count;
                if (count <= 0) continue;
                string text = Encoding.UTF8.GetString(buffer, 0, count);
                trace.WriteLine($"{DateTime.UtcNow:O} log-delta file={file.Name} bytes={count} from={offset}");
                runtime.WriteLine($"===== {DateTime.UtcNow:O} {file.Name} +{count} bytes =====");
                runtime.Write(text);
                if (!text.EndsWith("\n", StringComparison.Ordinal)) runtime.WriteLine();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                trace.WriteLine($"{DateTime.UtcNow:O} log-read-error file={Path.GetFileName(path)} {e.Message}");
            }
        }
    }

    private static int SafeHandleCount(Process process)
    {
        try { return process.HandleCount; } catch { return -1; }
    }

    private static long SafeCpuMilliseconds(Process process)
    {
        try { return (long)process.TotalProcessorTime.TotalMilliseconds; } catch { return -1; }
    }

    private static string DescribeModules(Process process)
    {
        try
        {
            return string.Join("\n", process.Modules.Cast<ProcessModule>()
                .OrderBy(m => m.ModuleName, StringComparer.OrdinalIgnoreCase)
                .Select(m => $"{m.ModuleName} base=0x{m.BaseAddress.ToInt64():X} size={m.ModuleMemorySize} path={m.FileName}"));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return "<module enumeration unavailable: " + e.Message + ">";
        }
    }

    private void CaptureRuntimeLogs()
    {
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
                                line.Contains("hero_dog_texture_readback", StringComparison.Ordinal) ||
                                line.Contains("[game-speed]", StringComparison.Ordinal) ||
                                line.Contains("audio endpoint", StringComparison.Ordinal) ||
                                line.Contains("[error]", StringComparison.OrdinalIgnoreCase) ||
                                line.Contains("[critical]", StringComparison.OrdinalIgnoreCase))
                                evidence.Add(file.Name + ": " + line);
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _notes.Add($"Log {file.Name}: {e.Message}"); }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _notes.Add($"Log listing: {e.Message}"); }
        _report["log_files"] = copiedLogs;
        WriteText("texture-runtime.txt", string.Join(Environment.NewLine, evidence));
    }

    public static string FormatExitCode(int code) => $"0x{unchecked((uint)code):X8}";
    internal static string DescribeExit(int? code) => code switch
    {
        null => "The launcher did not observe a process exit code.",
        0 => "The process exited successfully (0).",
        unchecked((int)0xC0000374) => "0xC0000374 / STATUS_HEAP_CORRUPTION: Windows detected heap corruption; the cause is not established by this report.",
        unchecked((int)0xC0000005) => "0xC0000005 / STATUS_ACCESS_VIOLATION: invalid memory access; the cause requires a native stack/dump.",
        _ => "Process exit code: " + FormatExitCode(code.Value)
    };

    private static object HashFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Hashing a reparse-point file is not supported.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new { size = stream.Length, sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() };
    }

    private void CapturePromptFiles(string folder)
    {
        // Copy only bounded logs/sidecars. Never copy SWF/BNK/game/save content.
        foreach (string root in new[] { _gameDataRoot, _gameDirectory, AppContext.BaseDirectory }.Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (string name in PromptLogs)
        {
            string source = Path.Combine(root, name);
            if (!File.Exists(source)) continue;
            try
            {
                string label = root.Equals(_gameDataRoot, StringComparison.OrdinalIgnoreCase) ? "game-data" :
                    root.Equals(_gameDirectory, StringComparison.OrdinalIgnoreCase) ? "runtime" : "launcher";
                CopyBounded(source, Path.Combine(DirectoryPath, folder, label, name), 2 * 1024 * 1024);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _notes.Add($"Prompt log {name}: {e.Message}"); }
        }
        var movies = new Dictionary<string, object?>();
        foreach (string game in new[] { "wizardstower", "keystone", "magedice" })
        {
            string path = Path.Combine(_gameDataRoot, "data", "pubgames", game, "game.swf");
            var files = new Dictionary<string, object?>();
            foreach (string suffix in new[] { "", ".xbox-prompts-backup" })
            {
                string source = path + suffix;
                if (!File.Exists(source)) { files["game.swf" + suffix] = "absent"; continue; }
                try
                {
                    if (new FileInfo(source).Length > 32 * 1024 * 1024) { files["game.swf" + suffix] = "too large; hash skipped"; continue; }
                    files["game.swf" + suffix] = HashFile(source);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { files["game.swf" + suffix] = e.Message; }
            }
            string state = path + ".launcher-pubgame-prompts.json";
            if (File.Exists(state))
            {
                try
                {
                    if (new FileInfo(state).Length <= 32768)
                        CopyBounded(state, Path.Combine(DirectoryPath, folder, game + "-prompt-state.json"), 32768);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _notes.Add("Prompt state: " + e.Message); }
            }
            movies[game] = files;
        }
        string marker = Path.Combine(_gameDataRoot, "pubgames-original-prompts.txt");
        try
        {
            if (File.Exists(marker) && new FileInfo(marker).Length <= 64)
                CopyBounded(marker, Path.Combine(DirectoryPath, folder, "pubgames-original-prompts.txt"), 64);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { _notes.Add("PubGames compatibility marker: " + e.Message); }
        WriteText(Path.Combine(folder, "swf-hashes.json"), JsonSerializer.Serialize(movies, new JsonSerializerOptions { WriteIndented = true }));
    }

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

    private void CaptureCrashDumps()
    {
        string? folder = _werDump.Folder;
        var matchingDumps = new List<FileInfo>();
        _report["crash_dump_files"] = Array.Empty<string>();
        _report["crash_dump_detected"] = false;
        _report["crash_dump_in_zip"] = false;

        if (folder is not null && _werDump.Configured && _pid is not null)
        {
            // WER can finish after the process exits. Match BOTH the PID and
            // session time, so an older crash with a recycled PID is excluded.
            // We never silently put raw process memory in the shareable ZIP.
            for (int attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    if (Directory.Exists(folder))
                        matchingDumps = Directory.EnumerateFiles(folder, "*.dmp", SearchOption.TopDirectoryOnly)
                            .Where(path => IsSessionDump(path, _pid.Value, _startedUtc))
                            .Select(path => new FileInfo(path))
                            .OrderByDescending(file => file.LastWriteTimeUtc)
                            .ToList();
                    if (matchingDumps.Count > 0 && matchingDumps.All(file => IsStableDump(file.FullName)))
                        break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _notes.Add("WER dump directory inaccessible: " + e.Message);
                    break;
                }
                Thread.Sleep(200);
            }
        }

        string[] found = matchingDumps.Where(file => IsStableDump(file.FullName))
            .Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _report["crash_dump_files"] = found;
        _report["crash_dump_detected"] = found.Length > 0;
        _report["crash_dump_in_zip"] = false;

        if (found.Length == 0)
            _notes.Add(_werDump.Configured
                ? "HKLM WER is configured but no matching crash dump was detected. Check permissions and AeDebug."
                : "WER LocalDumps is not configured in HKLM; use the optional administrator setup tool.");

        var text = new StringBuilder();
        text.AppendLine("Windows Error Reporting crash dump status (v23)");
        text.AppendLine("WER registry: " + _werDump.Status);
        text.AppendLine("Registry key: " + (_werDump.RegistryPath ?? "not configured"));
        text.AppendLine("Dump directory: " + (folder ?? "not configured"));
        text.AppendLine("Dump type: " + (_werDump.DumpType?.ToString(CultureInfo.InvariantCulture) ?? "not configured"));
        text.AppendLine("Session PID: " + (_pid?.ToString(CultureInfo.InvariantCulture) ?? "unknown"));
        text.AppendLine("Matching crash dumps: " + found.Length.ToString(CultureInfo.InvariantCulture));
        foreach (FileInfo file in matchingDumps.Where(file => found.Contains(file.Name, StringComparer.OrdinalIgnoreCase)))
            text.AppendLine($"  {file.FullName} | size={file.Length} | lastWriteUtc={file.LastWriteTimeUtc:O}");
        text.AppendLine("DMP files are intentionally NOT packaged into session ZIP: they may contain private memory, tokens and paths.");
        text.AppendLine("The launcher never writes registry keys; enable HKLM WER with explicit administrator consent.");
        text.AppendLine("If no dumps appear despite setup, check crash handler/AeDebug or use Microsoft ProcDump.");
        WriteText("crash-dump-status.txt", text.ToString());
    }

    private bool IsSessionDump(string path, int pid, DateTime startedUtc)
    {
        try
        {
            var file = new FileInfo(path);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length <= 0)
                return false;
            string prefix = Path.GetFileName(_executable) + "." + pid.ToString(CultureInfo.InvariantCulture);
            bool matchingName = file.Name.Equals(prefix + ".dmp", StringComparison.OrdinalIgnoreCase) ||
                file.Name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase) &&
                file.Extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase);
            return matchingName && file.LastWriteTimeUtc >= startedUtc.AddSeconds(-5);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsStableDump(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Length <= 0 || (file.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            long before = file.Length;
            Thread.Sleep(80);
            file.Refresh();
            return file.Length == before && file.Length > 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void CaptureWindowsCrashEvents()
    {
        if (!OperatingSystem.IsWindows() || _pid is null) return;
        try
        {
            string tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wevtutil.exe");
            var info = new ProcessStartInfo(tool)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.Unicode, StandardErrorEncoding = Encoding.Unicode
            };
            string start = _startedUtc.AddSeconds(-2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            info.ArgumentList.Add("qe"); info.ArgumentList.Add("Application");
            info.ArgumentList.Add("/q:*[System[(EventID=1000 or EventID=1001 or EventID=1002) and TimeCreated[@SystemTime>='" + start + "']]]");
            info.ArgumentList.Add("/c:64"); info.ArgumentList.Add("/rd:true");
            info.ArgumentList.Add("/f:xml"); info.ArgumentList.Add("/e:Events"); info.ArgumentList.Add("/uni:true");
            using var process = Process.Start(info) ?? throw new IOException("Could not query Windows crash events.");
            Task<string> output = ReadBoundedText(process.StandardOutput, 1024 * 1024);
            Task<string> error = ReadBoundedText(process.StandardError, 8192);
            if (!process.WaitForExit(6000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                _notes.Add("Windows crash-event query timed out; no system settings were changed.");
                return;
            }
            if (!Task.WaitAll(new Task[] { output, error }, 2000)) { _notes.Add("Windows crash-event output was incomplete."); return; }
            if (process.ExitCode != 0) { _notes.Add("Windows crash-event query: " + error.Result); return; }
            string selected = FilterApplicationEvents(output.Result, _executable, _pid.Value, _startedUtc);
            WriteText("windows-crash-events.xml", selected);
            _notes.Add("Windows events are best-effort; an empty result is not evidence that no crash occurred. WER mini-dump capture is handled separately.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or XmlException or AggregateException)
        { _notes.Add("Windows crash events unavailable: " + e.Message); }
    }

    private static async Task<string> ReadBoundedText(StreamReader reader, int limit)
    {
        var builder = new StringBuilder(); char[] buffer = new char[4096]; int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
        {
            int keep = Math.Min(n, Math.Max(0, limit - builder.Length));
            if (keep > 0) builder.Append(buffer, 0, keep);
            // Continue draining redirected output, but never grow the buffer past limit.
        }
        return builder.ToString();
    }

    internal static string FilterApplicationEvents(string xml, string executable, int pid, DateTime startedUtc)
    {
        var result = new XElement("Events", new XAttribute("scope", "game-process-only"));
        if (string.IsNullOrWhiteSpace(xml)) return result.ToString();
        using var reader = XmlReader.Create(new StringReader(xml.TrimStart('\uFEFF')), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        XDocument document = XDocument.Load(reader);
        string basename = executable.Replace('\\', '/').Split('/').Last();
        foreach (XElement evt in document.Descendants().Where(e => e.Name.LocalName == "Event"))
        {
            XElement? time = evt.Descendants().FirstOrDefault(e => e.Name.LocalName == "TimeCreated");
            if (!DateTime.TryParse(time?.Attribute("SystemTime")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime when) || when < startedUtc.AddSeconds(-2)) continue;
            XElement[] values = evt.Descendants().Where(e => e.Name.LocalName == "Data").ToArray();
            if (!values.Any(v => v.Value.Equals(basename, StringComparison.OrdinalIgnoreCase) ||
                v.Value.Equals(executable, StringComparison.OrdinalIgnoreCase))) continue;
            XElement? process = values.FirstOrDefault(v => v.Attribute("Name")?.Value is "ProcessId" or "ProcessID");
            if (process is not null)
            {
                string text = process.Value.Trim(); bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                if (!int.TryParse(hex ? text[2..] : text, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int eventPid) || eventPid != pid) continue;
            }
            result.Add(new XElement(evt));
        }
        return result.ToString();
    }

    private sealed class WerDumpConfiguration
    {
        private const string Root = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps";

        internal string Status { get; }
        internal string? Folder { get; }
        internal string? RegistryPath { get; }
        internal int? DumpType { get; }
        internal bool Configured => Status == "configured";

        private WerDumpConfiguration(string status, string? folder = null, string? key = null, int? type = null)
        { Status = status; Folder = folder; RegistryPath = key; DumpType = type; }

        internal static WerDumpConfiguration Detect(string executable, List<string> notes)
        {
            if (!OperatingSystem.IsWindows()) return new WerDumpConfiguration("not_windows");
            try
            {
                using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                    Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
                using RegistryKey? global = machine.OpenSubKey(Root, writable: false);
                string perExe = Root + "\\" + Path.GetFileName(executable);
                using RegistryKey? app = machine.OpenSubKey(perExe, writable: false);
                bool globalSettings = global?.GetValueNames().Any(name =>
                    name.Equals("DumpFolder", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("DumpType", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("DumpCount", StringComparison.OrdinalIgnoreCase)) == true;
                if (app is null && !globalSettings)
                {
                    notes.Add("WER HKLM LocalDumps not configured; launcher cannot enable it without administrator consent.");
                    return new WerDumpConfiguration("not_configured");
                }
                object? Value(string name) => app?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) ??
                    global?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

                string rawFolder = Value("DumpFolder") as string ?? @"%LOCALAPPDATA%\CrashDumps";
                string expanded = Environment.ExpandEnvironmentVariables(rawFolder);
                if (!Path.IsPathFullyQualified(expanded) || expanded.Contains('%'))
                {
                    notes.Add("WER DumpFolder is not a valid absolute path: " + rawFolder);
                    return new WerDumpConfiguration("invalid_folder", null, "HKLM\\" + (app is null ? Root : perExe));
                }
                string folder = Path.GetFullPath(expanded);
                int type = Value("DumpType") is int configuredType ? configuredType : 1;
                if (type is not (0 or 1 or 2))
                {
                    notes.Add("WER DumpType is invalid: " + type);
                    return new WerDumpConfiguration("invalid_type", folder, "HKLM\\" + (app is null ? Root : perExe), type);
                }
                if (!Directory.Exists(folder)) notes.Add("WER dump directory does not exist yet: " + folder);
                notes.Add("Detected supported HKLM WER LocalDumps configuration; actual dump creation will be checked after game exit.");
                return new WerDumpConfiguration("configured", folder, "HKLM\\" + (app is null ? Root : perExe), type);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or ArgumentException or NotSupportedException)
            {
                notes.Add("WER registry query failed: " + e.Message);
                return new WerDumpConfiguration("unavailable");
            }
        }
    }

    private void CopyBounded(string source, string target, int maxBytes)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) { _notes.Add("Reparse-point input skipped: " + Path.GetFileName(source)); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long size = input.Length;
        if (size > maxBytes) { input.Position = size - maxBytes; _notes.Add($"Tail only ({maxBytes} bytes): {Path.GetFileName(source)}"); }
        using (var output = File.Create(target))
        {
            byte[] buffer = new byte[81920]; int remaining = (int)Math.Min(size, maxBytes);
            while (remaining > 0)
            {
                int count = input.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (count == 0) break;
                output.Write(buffer, 0, count); remaining -= count;
            }
        }
        _capturedFiles.Add(target);
    }

    private void WriteText(string relative, string text)
    {
        string path = Path.Combine(DirectoryPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        _capturedFiles.Add(path);
    }

    private void SaveReport()
    {
        string path = Path.Combine(DirectoryPath, "session.json"); string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(tmp, path, true); _capturedFiles.Add(path);
    }

    private void CreateArchive()
    {
        string destination = DirectoryPath + ".zip";
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            _report["archive_status"] = "created";
            _report["archive_file"] = Path.GetFileName(destination);
            SaveReport();
            string root = Path.GetFileName(DirectoryPath);
            var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (string path in _capturedFiles.OrderBy(p => p, StringComparer.Ordinal))
                    {
                        // Fast exits can prevent a background trace from opening.
                        // Do not fail the entire ZIP for an absent optional file.
                        if (!File.Exists(path)) continue;
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing a report symlink: " + path);
                        string relative = Path.GetRelativePath(DirectoryPath, path).Replace('\\', '/');
                        if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) throw new IOException("Report entry outside the session.");
                        string name = root + "/" + relative;
                        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        byte[] hash = SHA256.HashData(input); input.Position = 0;
                        using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open(); input.CopyTo(entry);
                        hashes.Add(name, hash);
                    }
                }
                file.Flush(flushToDisk: true);
            }
            // Re-open and read EVERY entry, comparing it with the source bytes.
            using (var zip = ZipFile.OpenRead(temporary))
            {
                if (zip.Entries.Count != hashes.Count || zip.GetEntry(root + "/session.json") is null)
                    throw new InvalidDataException("Incomplete diagnostic ZIP.");
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    using var stream = entry.Open();
                    if (!hashes.TryGetValue(entry.FullName, out byte[]? expected) ||
                        !SHA256.HashData(stream).AsSpan().SequenceEqual(expected))
                        throw new InvalidDataException("Diagnostic ZIP verification failed: " + entry.FullName);
                }
            }
            // Do not overwrite a manually created archive; session names are unique.
            File.Move(temporary, destination, overwrite: false);
            ArchivePath = destination;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ArchiveError = e.Message;
            _report["archive_status"] = "failed"; _report["archive_error"] = e.Message;
            _notes.Add("ZIP could not be created. The uncompressed session folder was retained: " + e.Message);
            try { SaveReport(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
